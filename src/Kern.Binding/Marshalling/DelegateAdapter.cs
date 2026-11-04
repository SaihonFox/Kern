using System.Linq.Expressions;
using System.Reflection;
using Kern.Binding.Values;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Binding.Marshalling;

/// <summary>
/// Обёртка Kern-функции в делегат CLR (spec §12.10): <c>list.ForEach(fun(i32 x) ...)</c>.
/// Поддержано направление Kern → CLR; делегат, возвращённый из BCL, остаётся нативным
/// объектом и вызывается только через дополнительные обёртки библиотеки.
/// </summary>
internal static class DelegateAdapter
{
	private static readonly MethodInfo FromObjectMethod =
		typeof(KMarshal).GetMethod(nameof(KMarshal.FromObject), [typeof(object)])!;

	private static readonly MethodInfo ToMethod =
		typeof(KMarshal).GetMethod(nameof(KMarshal.To), [typeof(KValue), typeof(Type)])!;

	private static readonly MethodInfo KFuncInvokeMethod =
		typeof(KFunc).GetMethod(nameof(KFunc.Invoke), [typeof(Interpreter), typeof(KValue[])])!;

	/// <summary>Создать делегат <paramref name="delegateType"/>, вызывающий Kern-функцию.</summary>
	public static Delegate Create(KValue function, Type delegateType, Interpreter interpreter)
	{
		MethodInfo invoke = delegateType.GetMethod("Invoke")
			?? throw new KernPanic($"тип '{delegateType.Name}' не является делегатом");
		ParameterInfo[] parameters = invoke.GetParameters();

		var variables = new ParameterExpression[parameters.Length];
		var arguments = new Expression[parameters.Length];
		for (var index = 0; index < parameters.Length; index++)
		{
			variables[index] = Expression.Parameter(
				parameters[index].ParameterType,
				parameters[index].Name ?? $"arg{index}");
			arguments[index] = Expression.Call(
				FromObjectMethod,
				Expression.Convert(variables[index], typeof(object)));
		}

		ParameterExpression functionValue = Expression.Variable(typeof(KFunc), "function");
		Expression invokeCall = Expression.Call(
			functionValue,
			KFuncInvokeMethod,
			Expression.Constant(interpreter),
			Expression.NewArrayInit(typeof(KValue), arguments));
		Expression body = invoke.ReturnType == typeof(void)
			? Expression.Block(typeof(void), invokeCall)
			: Expression.Convert(
				Expression.Call(ToMethod, invokeCall, Expression.Constant(invoke.ReturnType)),
				invoke.ReturnType);

		LambdaExpression lambda = Expression.Lambda(
			delegateType,
			Expression.Block(
				new ParameterExpression[] { functionValue },
				Expression.Assign(
					functionValue,
					Expression.Constant(new KFunc(function), typeof(KFunc))),
				body),
			variables);
		return lambda.Compile();
	}
}
