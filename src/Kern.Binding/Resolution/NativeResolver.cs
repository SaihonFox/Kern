using System.Linq.Expressions;
using System.Reflection;
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
			if (descriptor.Method is null)
				continue;
			// Generic-метод закрывается в Build (реальные аргументы приходят через
			// `[KernTypeArgs]`), поэтому здесь он допустим.

			if (!descriptor.Method.IsStatic)
			{
				_diagnostics.Add($"native '{Kern.Vm.Native.NativeKey.Describe(descriptor.NativeKey)}': экземплярные члены пока не поддержаны");
				continue;
			}

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
				binding = null!;
				return false;
			}

			NativeBinding built = Build(descriptor);
			_cache[key] = built;
			binding = built;
			return true;
		}
	}

	private static NativeBinding Build(NativeFunctionDescriptor descriptor)
	{
		MethodInfo method = descriptor.Method
			?? throw new InvalidOperationException($"native '{Kern.Vm.Native.NativeKey.Describe(descriptor.NativeKey)}' не имеет CLR-метода");
		if (method.IsGenericMethodDefinition)
		{
			// Реальные аргументы типа приходят через `[KernTypeArgs]`; закрытие `object`
			// нужно только чтобы построить вызываемый delegate (сам T в теле не используется).
			method = method.MakeGenericMethod([.. method.GetGenericArguments().Select(_ => typeof(object))]);
		}

		ParameterInfo[] parameters = method.GetParameters();

		ParameterExpression interpreter = Expression.Parameter(typeof(Interpreter), "interpreter");
		ParameterExpression arguments = Expression.Parameter(typeof(KValue[]), "arguments");
		ParameterExpression typeArguments = Expression.Parameter(typeof(KType[]), "typeArguments");

		var callArguments = new List<Expression>(parameters.Length);
		var argumentIndex = 0;
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

		Expression call = Expression.Call(method, callArguments);
		Expression body = ConvertReturn(call, method.ReturnType, interpreter);
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
