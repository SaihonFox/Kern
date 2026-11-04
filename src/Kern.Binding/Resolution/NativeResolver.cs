using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Kern.Binding.Attributes;
using Kern.Binding.Catalog;
using Kern.Binding.Marshalling;
using Kern.Binding.Values;
using Kern.Bytecode;
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

	private static readonly MethodInfo FromHandleMethod = typeof(KMarshal)
		.GetMethod(nameof(KMarshal.FromHandle), [typeof(object)])!;

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
		string ownerName = DotnetCatalog.SimpleTypeName(type);
		IReadOnlyList<string>? typeParameters = type.IsGenericTypeDefinition
			? [.. type.GetGenericArguments().Select(argument => argument.Name)]
			: null;

		if (kind == NativeMemberKind.Constructor)
		{
			foreach (ConstructorInfo constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
			{
				if (constructor.GetParameters().Length != expected)
					continue;
				if (!DotnetCatalog.TryBuildConstructor(constructor, type, ownerName, typeParameters,
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
				? DotnetCatalog.TryBuild(method, type, ownerName, typeParameters, kind, out candidate)
				: DotnetCatalog.TryBuildInstance(method, type, ownerName, typeParameters, kind, out candidate);
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
		Type? declaring = descriptor.Method?.DeclaringType ?? descriptor.Constructor?.DeclaringType;
		bool openGeneric = declaring?.IsGenericTypeDefinition == true;
		if (!RuntimeFeature.IsDynamicCodeSupported || openGeneric || NeedsReflection(descriptor))
			return BuildReflection(descriptor);

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
		Expression body = ConvertReturn(call, returnType, interpreter, IsDotnetHandle(descriptor, returnType));
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

	/// <summary>
	/// Нужен ли рефлексионный путь вместо скомпилированного делегата: <c>ref</c>/<c>out</c>
	/// параметры, делегаты CLR и открытые generic-типы (их нельзя закрыть до вызова).
	/// </summary>
	private static bool NeedsReflection(NativeFunctionDescriptor descriptor)
	{
		ParameterInfo[] parameters = descriptor.Constructor?.GetParameters() ?? descriptor.Method?.GetParameters() ?? [];
		foreach (ParameterInfo parameter in parameters)
		{
			if (parameter.ParameterType.IsByRef)
				return true;
			if (typeof(Delegate).IsAssignableFrom(parameter.ParameterType))
				return true;
		}

		return false;
	}

	/// <summary>Результат — объект импортированного <c>use dotnet</c>-типа (нативный handle).</summary>
	private static bool IsDotnetHandle(NativeFunctionDescriptor descriptor, Type returnType) =>
		descriptor.NativeKey.StartsWith("dotnet:", StringComparison.Ordinal) &&
		DotnetCatalog.IsImportedClr(returnType);

	/// <summary>
	/// Рефлексионный вызов: <c>ref</c>/<c>out</c> с записью обратно в ячейку, делегаты CLR
	/// как обёртки Kern-функций и закрытие открытых generic-типов аргументами вызова.
	/// Используется, когда скомпилированный делегат невозможен или невыгоден (spec §12.10).
	/// </summary>
	private static NativeBinding BuildReflection(NativeFunctionDescriptor descriptor)
	{
		ConstructorInfo? constructor = descriptor.Constructor;
		MethodInfo? method = descriptor.Method;
		if (constructor is null && method is null)
			throw new KernPanic(
				$"native '{Kern.Vm.Native.NativeKey.Describe(descriptor.NativeKey)}': нет CLR-члена");
		if (method is { IsGenericMethodDefinition: true } && method.DeclaringType?.IsGenericTypeDefinition != true)
			throw new KernPanic(
				$"native '{Kern.Vm.Native.NativeKey.Describe(descriptor.NativeKey)}': generic-метод не поддерживается в NativeAOT");

		Type? declaring = method?.DeclaringType ?? constructor!.DeclaringType;
		bool openGeneric = declaring?.IsGenericTypeDefinition == true;
		bool instance = constructor is null && !method!.IsStatic;
		Type? selfType = instance ? descriptor.SelfClrType ?? declaring : null;
		string description = Kern.Vm.Native.NativeKey.Describe(descriptor.NativeKey);

		return new NativeBinding
		{
			ParameterCount = descriptor.SignatureParameterCount,
			Invoke = (interpreter, arguments, typeArguments) =>
			{
				try
				{
					MethodInfo? callMethod = method;
					ConstructorInfo? callConstructor = constructor;
					Type? receiverType = selfType;
					if (openGeneric)
					{
						Type[] genericArguments = CloseGenericArguments(
							descriptor, typeArguments, arguments, instance);
						Type closed = declaring!.MakeGenericType(genericArguments);
						receiverType = instance ? closed : null;
						callMethod = method is null ? null : CloseMethod(method, closed);
						callConstructor = constructor is null ? null : CloseConstructor(constructor, closed);
						if ((method is not null && callMethod is null) ||
							(constructor is not null && callConstructor is null))
						{
							throw new KernPanic(
								$"нативный вызов '{description}': не удалось закрыть generic-тип '{closed}'");
						}
					}

					var argumentIndex = 0;
					object? target = null;
					if (instance)
						target = KMarshal.To(arguments[argumentIndex++], receiverType!);

					ParameterInfo[] parameters = callConstructor?.GetParameters() ?? callMethod!.GetParameters();
					var callArguments = new object?[parameters.Length];
					var byRefTargets = new List<(int Index, IValueCell Cell, Type Element)>();
					for (var index = 0; index < parameters.Length; index++)
					{
						ParameterInfo parameter = parameters[index];
						if (parameter.ParameterType == typeof(Kern.Vm.Execution.Interpreter))
						{
							callArguments[index] = interpreter;
							continue;
						}

						if (parameter.GetCustomAttribute<KernTypeArgsAttribute>() is not null)
						{
							callArguments[index] = typeArguments;
							continue;
						}

						Type parameterType = parameter.ParameterType;
						if (parameterType.IsByRef)
						{
							Type element = parameterType.GetElementType()!;
							KValue cellValue = arguments[argumentIndex++];
							if (cellValue.Ref is not IValueCell cell)
							{
								throw new KernPanic(
									$"нативный вызов '{description}': параметр '{parameter.Name}' объявлен как ref/out — передайте '*переменная' или '*поле'");
							}

							callArguments[index] = InitialCellValue(cell.Get(), element);
							byRefTargets.Add((index, cell, element));
							continue;
						}

						callArguments[index] = ConvertArgument(
							arguments[argumentIndex++], parameterType, interpreter, description);
					}

					object? result = callConstructor is not null
						? callConstructor.Invoke(callArguments)
						: callMethod!.Invoke(target, callArguments);
					foreach (var (index, cell, element) in byRefTargets)
						cell.Set(callArguments[index] is null
							? DefaultValue(element)
							: KMarshal.FromObject(callArguments[index]));

					Type returnType = callConstructor?.DeclaringType ?? callMethod!.ReturnType;
					if (returnType == typeof(void))
						return KValue.Void;
					if (result is KTask task)
						return KMarshal.FromKTask(task);
					if (IsDotnetHandle(descriptor, returnType) ||
						(result is not null && DotnetCatalog.IsImportedClr(result.GetType())))
						return KMarshal.FromHandle(result);
					return KMarshal.FromReturn(result);
				}
				catch (KernPanic)
				{
					throw;
				}
				catch (TargetInvocationException exception) when (exception.InnerException is not null)
				{
					throw new KernPanic(
						$"нативный вызов '{description}' завершился ошибкой: {exception.InnerException.Message}",
						exception.InnerException);
				}
				catch (Exception exception)
				{
					throw new KernPanic(
						$"нативный вызов '{description}' завершился ошибкой: {exception.Message}",
						exception);
				}
			},
		};
	}

	/// <summary>Начальное значение ref/out-параметра: содержимое ячейки или default(T).</summary>
	private static object? InitialCellValue(KValue value, Type element) =>
		value.Kind is ValueKind.None or ValueKind.Void or ValueKind.Nil
			? DefaultValue(element)
			: KMarshal.To(value, element);

	private static KValue DefaultValue(Type type) =>
		type.IsValueType ? KMarshal.FromObject(Activator.CreateInstance(type)) : KValue.Nil;

	/// <summary>
	/// Kern-значение → CLR-аргумент. Делегат CLR получает обёртку Kern-функции; остальные
	/// типы идут обычным маршаллингом (spec §12.4).
	/// </summary>
	private static object? ConvertArgument(
		KValue value,
		Type target,
		Interpreter interpreter,
		string description)
	{
		if (!typeof(Delegate).IsAssignableFrom(target))
			return KMarshal.To(value, target);

		if (value.Ref is not (KClosure or Kern.Bytecode.FunctionDef))
		{
			throw new KernPanic(
				$"нативный вызов '{description}': ожидался делегат '{target.Name}', получено {value.TypeName}");
		}

		if (!RuntimeFeature.IsDynamicCodeSupported)
			throw new KernPanic($"нативный вызов '{description}': делегаты CLR не поддержаны в NativeAOT");
		return DelegateAdapter.Create(value, target, interpreter);
	}

	/// <summary>Аргументы открытого generic-типа: reified-типы вызова или тип получателя.</summary>
	private static Type[] CloseGenericArguments(
		NativeFunctionDescriptor descriptor,
		KType[] typeArguments,
		KValue[] arguments,
		bool instance)
	{
		int arity = descriptor.GenericParameters.Count;
		if (arity == 0)
			return [];

		var result = new Type[arity];
		if (typeArguments.Length >= arity)
		{
			for (var index = 0; index < arity; index++)
				result[index] = ClrType(typeArguments[index]) ?? throw new KernPanic(
					$"нативный вызов '{Kern.Vm.Native.NativeKey.Describe(descriptor.NativeKey)}': " +
					$"generic-аргумент '{typeArguments[index].Canonical}' не имеет CLR-представления");
			return result;
		}

		if (instance && arguments.Length > 0)
		{
			object? receiver = arguments[0].Ref is KNativeBox box ? box.Value : arguments[0].Ref;
			Type? actual = receiver?.GetType();
			if (actual is { IsGenericType: true } && actual.GetGenericArguments().Length == arity)
				return actual.GetGenericArguments();
		}

		throw new KernPanic(
			$"нативный вызов '{Kern.Vm.Native.NativeKey.Describe(descriptor.NativeKey)}': " +
			"не удалось определить аргументы открытого generic-типа");
	}

	/// <summary>Reified-тип VM → CLR-тип (для закрытия generic-типов <c>use dotnet</c>).</summary>
	private static Type? ClrType(KType type) => type switch
	{
		KPrimType primitive => primitive.Prim switch
		{
			PrimKind.Bool => typeof(bool),
			PrimKind.Char => typeof(char),
			PrimKind.Str => typeof(string),
			PrimKind.I8 => typeof(sbyte),
			PrimKind.I16 => typeof(short),
			PrimKind.I32 => typeof(int),
			PrimKind.I64 => typeof(long),
			PrimKind.U8 => typeof(byte),
			PrimKind.U16 => typeof(ushort),
			PrimKind.U32 => typeof(uint),
			PrimKind.U64 => typeof(ulong),
			PrimKind.F16 => typeof(Half),
			PrimKind.F32 => typeof(float),
			PrimKind.F64 => typeof(double),
			PrimKind.Object => typeof(object),
			_ => null,
		},
		KClassType @class => DotnetCatalog.FindImportedType(@class.Definition.Name),
		KNullableType nullable => ClrType(nullable.Inner) is { IsValueType: true } inner
			? typeof(Nullable<>).MakeGenericType(inner)
			: null,
		KBuiltinGenericType builtin => CloseBuiltin(builtin),
		_ => null,
	};

	private static Type? CloseBuiltin(KBuiltinGenericType builtin)
	{
		Type definition = builtin.Builtin switch
		{
			BuiltinGenericKind.List => typeof(KList<>),
			BuiltinGenericKind.Array => typeof(KArray<>),
			BuiltinGenericKind.Dict => typeof(KDict<,>),
			_ => null!,
		};
		if (definition is null)
			return null;

		var arguments = new Type[builtin.TypeArgs.Length];
		for (var index = 0; index < arguments.Length; index++)
		{
			if (ClrType(builtin.TypeArgs[index]) is not { } argument)
				return null;
			arguments[index] = argument;
		}

		return definition.MakeGenericType(arguments);
	}

	/// <summary>Метод закрытого generic-типа: тот же metadata token, что у открытого определения.</summary>
	private static MethodInfo? CloseMethod(MethodInfo open, Type closed)
	{
		foreach (MethodInfo candidate in closed.GetMethods(
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
		{
			if (candidate.MetadataToken == open.MetadataToken &&
				candidate.Module == open.Module &&
				candidate.Name == open.Name)
				return candidate;
		}

		return null;
	}

	private static ConstructorInfo? CloseConstructor(ConstructorInfo open, Type closed)
	{
		foreach (ConstructorInfo candidate in closed.GetConstructors(
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
		{
			if (candidate.MetadataToken == open.MetadataToken && candidate.Module == open.Module)
				return candidate;
		}

		return null;
	}

	private static Expression ConvertReturn(
		Expression call,
		Type returnType,
		ParameterExpression interpreter,
		bool handle = false)
	{
		if (returnType == typeof(void))
			return Expression.Block(call, Expression.Constant(KValue.Void));

		if (handle)
			return Expression.Call(FromHandleMethod, Expression.Convert(call, typeof(object)));

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
