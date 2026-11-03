using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Kern.Binding.Attributes;
using Kern.Binding.Catalog;
using Kern.Binding.Marshalling;
using Kern.Binding.Values;
using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Native;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Binding.Resolution;

/// <summary>
/// Резолвер нативных привязок: <c>NativeKey</c> → скомпилированный делегат.
/// Делегаты строятся через <see cref="Expression"/> (spec §12.9), без
/// <see cref="MethodInfo.Invoke"/> на каждом вызове.
/// </summary>
public sealed class NativeResolver : INativeResolver
{
	private static readonly MethodInfo ToMethod = typeof(KMarshal)
		.GetMethods(BindingFlags.Public | BindingFlags.Static)
		.Single(method => method.Name == nameof(KMarshal.To) && method.IsGenericMethodDefinition);

	private static readonly MethodInfo FromMethod = typeof(KMarshal)
		.GetMethods(BindingFlags.Public | BindingFlags.Static)
		.Single(method => method.Name == nameof(KMarshal.From) && method.IsGenericMethodDefinition);

	private static readonly MethodInfo FromTaskMethod = typeof(KMarshal)
		.GetMethods(BindingFlags.Public | BindingFlags.Static)
		.Single(method => method.Name == nameof(KMarshal.FromTask) && method.IsGenericMethodDefinition);

	private static readonly MethodInfo FromPlainTaskMethod = typeof(KMarshal)
		.GetMethod(nameof(KMarshal.FromTask), [typeof(Task)])!;

	private static readonly MethodInfo FromKTaskMethod = typeof(KMarshal)
		.GetMethod(nameof(KMarshal.FromKTask))!;

	private static readonly MethodInfo FromKResultMethod = typeof(KMarshal)
		.GetMethods(BindingFlags.Public | BindingFlags.Static)
		.Single(method => method.Name == nameof(KMarshal.FromKResult));

	private readonly Dictionary<string, NativeFunctionDescriptor> _descriptors = new(StringComparer.Ordinal);
	private readonly Dictionary<string, NativeBinding> _cache = new(StringComparer.Ordinal);
	private readonly Lock _gate = new();
	private readonly List<string> _diagnostics = [];

	public NativeResolver(NativeCatalog catalog)
	{
		ArgumentNullException.ThrowIfNull(catalog);
		foreach (NativeFunctionDescriptor descriptor in catalog.Functions)
		{
			if (descriptor.Method is null && descriptor.Constructor is null)
				continue;
			// Generic-метод закрывается в Build (реальные аргументы приходят через
			// `[KernTypeArgs]`), поэтому здесь он допустим.

			if (!_descriptors.TryAdd(descriptor.NativeKey, descriptor))
				_diagnostics.Add($"native '{Kern.Vm.Native.NativeKey.Describe(descriptor.NativeKey)}': ключ встречается несколько раз");
		}
	}

	public IReadOnlyList<string> Diagnostics => _diagnostics;

	/// <summary>Сколько привязок зарегистрировано (для диагностики/тестов).</summary>
	public int Count => _descriptors.Count;

	public bool TryResolve(string key, out NativeBinding binding)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		lock (_gate)
		{
			if (_cache.TryGetValue(key, out NativeBinding? cached))
			{
				binding = cached;
				return true;
			}

			if (!_descriptors.TryGetValue(key, out NativeFunctionDescriptor? descriptor))
			{
				if (!TryBuildDotnet(key, out descriptor))
				{
					binding = null!;
					return false;
				}
			}

			NativeBinding built = Build(descriptor);
			_cache[key] = built;
			binding = built;
			return true;
		}
	}

	/// <summary>
	/// Резервный путь для <c>use dotnet...</c>: в <c>.kbc</c> хранится только ключ
	/// <c>dotnet:&lt;тип&gt;|имя|…</c>, поэтому тип и метод находятся рефлексией при первом вызове.
	/// </summary>
	private static bool TryBuildDotnet(string key, out NativeFunctionDescriptor descriptor)
	{
		descriptor = null!;
		if (!key.StartsWith("dotnet:", StringComparison.Ordinal))
			return false;
		string[] parts = key.Split('|');
		if (parts.Length != 5)
			return false;
		string typeName = parts[0]["dotnet:".Length..];
		string name = parts[1];
		NativeMemberKind kind = parts[2] switch
		{
			"g" => NativeMemberKind.PropertyGet,
			"s" => NativeMemberKind.PropertySet,
			"c" => NativeMemberKind.Constructor,
			_ => NativeMemberKind.Method,
		};
		int expected = parts[4].Length == 0 ? 0 : parts[4].Split(',').Length;

		Type? type = DotnetCatalog.FindType(typeName);
		if (type is null)
			return false;

		if (kind == NativeMemberKind.Constructor)
		{
			foreach (ConstructorInfo constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
			{
				if (constructor.GetParameters().Length != expected)
					continue;
				if (!DotnetCatalog.TryBuildConstructor(constructor, type, type.Name,
					out NativeFunctionDescriptor? candidate))
					continue;
				if (candidate!.NativeKey != key)
					continue;
				descriptor = candidate;
				return true;
			}
			return false;
		}

		foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
		{
			if (method.IsGenericMethodDefinition || method.DeclaringType == typeof(object))
				continue;
			if (kind == NativeMemberKind.PropertyGet && !method.Name.StartsWith("get_", StringComparison.Ordinal))
				continue;
			if (kind == NativeMemberKind.PropertySet && !method.Name.StartsWith("set_", StringComparison.Ordinal))
				continue;
			if (kind == NativeMemberKind.Method && method.IsSpecialName)
				continue;
			int parameterCount = method.GetParameters().Length;
			if (parameterCount != expected && !(method.IsStatic == false && parameterCount + 1 == expected))
				continue;

			NativeFunctionDescriptor? candidate;
			bool built = method.IsStatic
				? DotnetCatalog.TryBuild(method, type, type.Name, kind, out candidate)
				: DotnetCatalog.TryBuildInstance(method, type, type.Name, kind, out candidate);
			if (!built)
				continue;
			if (candidate!.Name != name && !method.Name.Equals(name, StringComparison.Ordinal))
				continue;
			if (candidate.NativeKey != key)
				continue;
			descriptor = candidate;
			return true;
		}
		return false;
	}

	private static NativeBinding Build(NativeFunctionDescriptor descriptor)
	{
		if (!RuntimeFeature.IsDynamicCodeSupported)
			return BuildWithoutDynamicCode(descriptor);

		ConstructorInfo? constructor = descriptor.Constructor;
		MethodInfo? method = descriptor.Method;
		if (constructor is null && method is null)
			throw new InvalidOperationException(
				$"native '{Kern.Vm.Native.NativeKey.Describe(descriptor.NativeKey)}' не имеет CLR-метода");

		if (method is { IsGenericMethodDefinition: true })
		{
			// Реальные аргументы типа приходят через `[KernTypeArgs]`; закрытие `object`
			// нужно только чтобы построить вызываемый delegate (сам T в теле не используется).
			method = method.MakeGenericMethod([.. method.GetGenericArguments().Select(_ => typeof(object))]);
		}

		ParameterInfo[] parameters = constructor is not null ? constructor.GetParameters() : method!.GetParameters();
		Type? selfType = constructor is null && !method!.IsStatic
			? descriptor.SelfClrType ?? method.DeclaringType
			: null;

		ParameterExpression interpreter = Expression.Parameter(typeof(Interpreter), "interpreter");
		ParameterExpression arguments = Expression.Parameter(typeof(KValue[]), "arguments");
		ParameterExpression typeArguments = Expression.Parameter(typeof(KType[]), "typeArguments");

		Expression? receiver = null;
		var argumentIndex = 0;
		if (selfType is not null)
		{
			receiver = Expression.Call(
				ToMethod.MakeGenericMethod(selfType),
				Expression.ArrayIndex(arguments, Expression.Constant(0)));
			argumentIndex = 1;
		}

		var callArguments = new List<Expression>(parameters.Length);
		foreach (ParameterInfo parameter in parameters)
		{
			if (parameter.ParameterType == typeof(Interpreter))
			{
				callArguments.Add(interpreter);
				continue;
			}

			if (parameter.GetCustomAttribute<KernTypeArgsAttribute>() is not null)
			{
				callArguments.Add(typeArguments);
				continue;
			}

			Expression raw = Expression.ArrayIndex(arguments, Expression.Constant(argumentIndex++));
			callArguments.Add(Expression.Call(ToMethod.MakeGenericMethod(parameter.ParameterType), raw));
		}

		if (argumentIndex != descriptor.SignatureParameterCount)
			throw new InvalidOperationException(
				$"native '{Kern.Vm.Native.NativeKey.Describe(descriptor.NativeKey)}': ожидалось {descriptor.SignatureParameterCount} аргументов, метод принимает {argumentIndex}");

		Expression call = constructor is not null
			? Expression.New(constructor, callArguments)
			: receiver is null
				? Expression.Call(method!, callArguments)
				: Expression.Call(receiver, method!, callArguments);
		Type returnType = constructor is not null ? constructor.DeclaringType! : method!.ReturnType;
		Expression body = ConvertReturn(call, returnType, interpreter);
		var compiled = Expression.Lambda<NativeInvoke>(body, interpreter, arguments, typeArguments).Compile();

		return new NativeBinding
		{
			ParameterCount = argumentIndex,
			Invoke = (currentInterpreter, values, typeArgs) =>
			{
				try
				{
					return compiled(currentInterpreter, values, typeArgs);
				}
				catch (KernPanic)
				{
					throw;
				}
				catch (Exception exception)
				{
					throw new KernPanic(
						$"нативный вызов '{Kern.Vm.Native.NativeKey.Describe(descriptor.NativeKey)}' завершился ошибкой: {exception.Message}",
						exception);
				}
			},
		};
	}

	private static NativeBinding BuildWithoutDynamicCode(NativeFunctionDescriptor descriptor)
	{
		ConstructorInfo? constructor = descriptor.Constructor;
		MethodInfo? method = descriptor.Method;
		if (constructor is null && method is null)
			throw new KernPanic(
				$"native '{Kern.Vm.Native.NativeKey.Describe(descriptor.NativeKey)}': нет CLR-члена");
		if (method is { IsGenericMethodDefinition: true })
			throw new KernPanic(
				$"native '{Kern.Vm.Native.NativeKey.Describe(descriptor.NativeKey)}': generic-натив не поддерживается в NativeAOT");

		ParameterInfo[] parameters = constructor is not null ? constructor.GetParameters() : method!.GetParameters();
		Type? selfType = constructor is null && !method!.IsStatic
			? descriptor.SelfClrType ?? method.DeclaringType
			: null;
		Type returnType = constructor is not null ? constructor.DeclaringType! : method!.ReturnType;

		return new NativeBinding
		{
			ParameterCount = descriptor.SignatureParameterCount,
			Invoke = (interpreter, arguments, typeArguments) =>
			{
				try
				{
					var argumentIndex = 0;
					object? target = null;
					if (selfType is not null)
						target = KMarshal.To(arguments[argumentIndex++], selfType);

					var callArguments = new object?[parameters.Length];
					for (var index = 0; index < parameters.Length; index++)
					{
						ParameterInfo parameter = parameters[index];
						if (parameter.ParameterType == typeof(Interpreter))
							callArguments[index] = interpreter;
						else if (parameter.GetCustomAttribute<KernTypeArgsAttribute>() is not null)
							callArguments[index] = typeArguments;
						else
							callArguments[index] = KMarshal.To(arguments[argumentIndex++], parameter.ParameterType);
					}

					object? result = constructor is not null
						? constructor.Invoke(callArguments)
						: method!.Invoke(target, callArguments);
					if (returnType == typeof(void))
						return KValue.Void;
					if (result is KTask task)
						return KMarshal.FromKTask(task);
					return KMarshal.FromReturn(result);
				}
				catch (KernPanic)
				{
					throw;
				}
				catch (TargetInvocationException exception) when (exception.InnerException is not null)
				{
					throw new KernPanic(
						$"нативный вызов '{Kern.Vm.Native.NativeKey.Describe(descriptor.NativeKey)}' завершился ошибкой: {exception.InnerException.Message}",
						exception.InnerException);
				}
				catch (Exception exception)
				{
					throw new KernPanic(
						$"нативный вызов '{Kern.Vm.Native.NativeKey.Describe(descriptor.NativeKey)}' завершился ошибкой: {exception.Message}",
						exception);
				}
			},
		};
	}

	private static Expression ConvertReturn(Expression call, Type returnType, ParameterExpression interpreter)
	{
		if (returnType == typeof(void))
			return Expression.Block(call, Expression.Constant(KValue.Void));

		if (returnType == typeof(KTask))
		{
			return Expression.Call(
				FromKTaskMethod,
				Expression.Convert(call, typeof(KTask)));
		}

		if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(KResult<,>))
		{
			// `result<T,E>` в Kern — union `Ok<T>`/`Err<E>` (spec §16): reified-типы
			// создаются в пуле интерпретатора, которому и передаётся результат.
			Type[] arguments = returnType.GetGenericArguments();
			return Expression.Call(
				FromKResultMethod.MakeGenericMethod(arguments),
				call,
				interpreter);
		}

		if (returnType == typeof(Task))
			return Expression.Call(FromPlainTaskMethod, Expression.Convert(call, typeof(Task)));

		if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
		{
			Type resultType = returnType.GetGenericArguments()[0];
			return Expression.Call(
				FromTaskMethod.MakeGenericMethod(resultType),
				Expression.Convert(call, typeof(Task<>).MakeGenericType(resultType)));
		}

		return Expression.Call(FromMethod.MakeGenericMethod(returnType), call);
	}
}
