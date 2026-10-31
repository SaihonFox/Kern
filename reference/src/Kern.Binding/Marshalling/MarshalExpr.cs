using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Kern.Binding.Wrappers;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Binding.Marshalling;

/// <summary>Строит Expression-фрагменты преобразований KValue ↔ CLR для заданного типа.</summary>
public static class MarshalExpr
{
	private static MethodInfo M(string name) => typeof(Marshaller).GetMethod(name)!;
	private static MethodInfo KV(string name, Type arg) => typeof(KValue).GetMethod(name, [arg])!;

	private static readonly MethodInfo IsNilM = M(nameof(Marshaller.IsNil));

	private static readonly Dictionary<Type, string> ToKValueName = new()
	{
		[typeof(bool)] = nameof(KValue.Bool), [typeof(char)] = nameof(KValue.Char),
		[typeof(sbyte)] = nameof(KValue.I8), [typeof(short)] = nameof(KValue.I16), [typeof(int)] = nameof(KValue.I32), [typeof(long)] = nameof(KValue.I64),
		[typeof(byte)] = nameof(KValue.U8), [typeof(ushort)] = nameof(KValue.U16), [typeof(uint)] = nameof(KValue.U32), [typeof(ulong)] = nameof(KValue.U64),
		[typeof(Half)] = nameof(KValue.F16), [typeof(float)] = nameof(KValue.F32), [typeof(double)] = nameof(KValue.F64),
	};

	/// <summary>Контекст вызова: параметры vm и typeArgs скомпилированного делегата.</summary>
	public sealed record Ctx(ParameterExpression Vm, ParameterExpression TypeArgs);

	private static bool IsWrapper(Type t) =>
		t.IsValueType && t.Namespace == typeof(KList<>).Namespace
		&& t.GetConstructors() is [var c] && c.GetParameters().Length == 1;

	private static bool IsListLike(Type t, out Type elem)
	{
		elem = typeof(object);
		if (t.IsArray) { elem = t.GetElementType()!; return true; }
		if (!t.IsGenericType) return false;
		var d = t.GetGenericTypeDefinition();
		if (d == typeof(List<>) || d == typeof(IList<>) || d == typeof(IReadOnlyList<>) || d == typeof(IEnumerable<>)
			|| d == typeof(ICollection<>) || d == typeof(IReadOnlyCollection<>))
		{ elem = t.GetGenericArguments()[0]; return true; }
		return false;
	}

	// =============== KValue → CLR ===============

	public static Expression ToClr(Expression kv, Type t, Ctx ctx)
	{
		if (t == typeof(KValue)) return kv;
		if (t == typeof(bool)) return Expression.Call(M(nameof(Marshaller.ToBool)), kv);
		if (t == typeof(char)) return Expression.Call(M(nameof(Marshaller.ToChar)), kv);
		if (t.IsEnum)
			return Expression.Convert(Expression.Convert(Expression.Call(M(nameof(Marshaller.EnumBits)), kv), Enum.GetUnderlyingType(t)), t);
		if (Nullable.GetUnderlyingType(t) is { } u)
			return Expression.Condition(Expression.Call(IsNilM, kv), Expression.Default(t), Expression.Convert(ToClr(kv, u, ctx), t));
		if (t == typeof(sbyte) || t == typeof(short) || t == typeof(int) || t == typeof(long)
			|| t == typeof(byte) || t == typeof(ushort) || t == typeof(uint) || t == typeof(ulong))
			return Expression.Convert(Expression.Call(M(nameof(Marshaller.ToI64)), kv), t);
		if (t == typeof(float) || t == typeof(double) || t == typeof(Half))
			return Expression.Convert(Expression.Call(M(nameof(Marshaller.ToF64)), kv), t);
		if (t == typeof(decimal)) return Expression.Call(M(nameof(Marshaller.ToDecimal)), kv);
		if (t == typeof(BigInteger)) return Expression.Call(M(nameof(Marshaller.ToBigInt)), kv);
		if (t == typeof(string)) return Expression.Call(M(nameof(Marshaller.ToStr)), kv);
		if (t == typeof(KType)) return Expression.Call(M(nameof(Marshaller.ToKType)), kv);
		if (t == typeof(object)) return Expression.Call(M(nameof(Marshaller.ToObject)), kv);
		if (t == typeof(Interpreter)) return ctx.Vm;
		if (t == typeof(KFunc.Callable)) return Expression.Call(M(nameof(Marshaller.MakeCallable)), ctx.Vm, kv);

		if (IsWrapper(t))
		{
			var ctor = t.GetConstructors()[0];
			var pt = ctor.GetParameters()[0].ParameterType;
			Expression inner = pt == typeof(KFunc.Callable)
				? Expression.Call(M(nameof(Marshaller.MakeCallable)), ctx.Vm, kv)
				: Expression.Call(M(nameof(Marshaller.RefOf)).MakeGenericMethod(pt), kv);
			return Expression.New(ctor, inner);
		}

		if (IsListLike(t, out var elem))
		{
			var p = Expression.Parameter(typeof(KValue), "e");
			var conv = Expression.Lambda(typeof(Func<,>).MakeGenericType(typeof(KValue), elem), ToClr(p, elem, ctx), p);
			Expression list = Expression.Call(M(nameof(Marshaller.ToSeq)).MakeGenericMethod(elem), kv, conv);
			return t.IsArray ? Expression.Call(list, typeof(List<>).MakeGenericType(elem).GetMethod("ToArray")!) : Expression.Convert(list, t);
		}

		return Expression.Call(
			(t.IsValueType ? M(nameof(Marshaller.StructOf)) : M(nameof(Marshaller.ClassOf))).MakeGenericMethod(t), kv);
	}

	// =============== CLR → KValue ===============
	// x должен быть ParameterExpression/Variable (может вычисляться несколько раз).

	public static Expression FromClr(Expression x, Type t)
	{
		if (t == typeof(KValue)) return x;
		if (ToKValueName.TryGetValue(t, out var name)) return Expression.Call(KV(name, t), x);
		if (t.IsEnum)
			return Expression.Call(KV(nameof(KValue.I64), typeof(long)), Expression.Convert(x, typeof(long)));
		if (Nullable.GetUnderlyingType(t) is { } u)
			return Expression.Condition(Expression.Property(x, "HasValue"),
				FromClr(Expression.Property(x, "Value"), u), Expression.Constant(KValue.Nil));
		if (t == typeof(string)) return Expression.Call(M(nameof(Marshaller.FromStr)), x);
		if (t == typeof(decimal)) return Expression.Call(M(nameof(Marshaller.FromDecimal)), x);
		if (t == typeof(BigInteger)) return Expression.Call(M(nameof(Marshaller.FromBigInt)), x);
		if (t == typeof(KType)) return Expression.Call(M(nameof(Marshaller.FromKType)), x);
		if (IsWrapper(t))
		{
			var inner = Expression.Property(x, "Inner");
			// KFunc/KAction: Inner — Callable, значение функции лежит в .Function
			return inner.Type == typeof(KFunc.Callable)
				? Expression.Property(inner, nameof(KFunc.Callable.Function))
				: Expression.Call(typeof(KValue).GetMethod(nameof(KValue.Obj))!, Expression.Convert(inner, typeof(object)));
		}
		if (IsListLike(t, out var elem) && t != typeof(string))
		{
			var p = Expression.Parameter(elem, "e");
			var conv = Expression.Lambda(typeof(Func<,>).MakeGenericType(elem, typeof(KValue)), FromClr(p, elem), p);
			return Expression.Call(M(nameof(Marshaller.FromSeq)).MakeGenericMethod(elem),
				Expression.Convert(x, typeof(IEnumerable<>).MakeGenericType(elem)), conv);
		}
		return Expression.Call(M(nameof(Marshaller.FromObject)), Expression.Convert(x, typeof(object)));
	}

	/// <summary>Возврат из async-метода: Task/Task&lt;T&gt;/ValueTask/ValueTask&lt;T&gt; → KTask.</summary>
	public static Expression FromTask(Expression call, Type ret)
	{
		if (ret == typeof(Task)) return Expression.Call(M(nameof(Marshaller.FromTask)), call);
		if (ret == typeof(ValueTask)) return Expression.Call(M(nameof(Marshaller.FromValueTask)), call);
		var inner = ret.GetGenericArguments()[0];
		var p = Expression.Parameter(inner, "r");
		var conv = Expression.Lambda(typeof(Func<,>).MakeGenericType(inner, typeof(KValue)), FromClr(p, inner), p);
		var name = ret.GetGenericTypeDefinition() == typeof(Task<>) ? nameof(Marshaller.FromTaskOf) : nameof(Marshaller.FromValueTaskOf);
		return Expression.Call(M(name).MakeGenericMethod(inner), call, conv);
	}
}
