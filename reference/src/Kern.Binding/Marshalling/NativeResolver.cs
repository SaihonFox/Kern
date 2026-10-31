using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Kern.Binding.Attributes;
using Kern.Binding.Catalog;
using Kern.Binding.Wrappers;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Binding.Marshalling;

/// <summary>
/// Мост VM → C#: по <see cref="NativeKey"/> находит член и строит <see cref="NativeInvoker"/> на скомпилированном
/// Expression-дереве (без MethodInfo.Invoke). Результат кэшируется. Generic-методы/типы инстанцируются с T = KValue.
/// </summary>
public sealed class NativeResolver : INativeResolver
{
	private delegate KValue Core(Interpreter vm, KValue[] args, KType[] typeArgs);

	private readonly ConcurrentDictionary<string, NativeInvoker> _cache = new();

	public NativeInvoker Resolve(string nativeKey) => _cache.GetOrAdd(nativeKey, Build);

	private static NativeInvoker Build(string key)
	{
		var member = NativeKey.Find(key) ?? throw new KernPanic($"нативный член не найден: {key}");
		var vm = Expression.Parameter(typeof(Interpreter), "vm");
		var args = Expression.Parameter(typeof(KValue[]), "args");
		var targs = Expression.Parameter(typeof(KType[]), "targs");
		var ctx = new MarshalExpr.Ctx(vm, targs);

		bool errResult = false;
		Expression body = member switch
		{
			FieldInfo f => BuildField(f, NativeKey.IsFieldSetter(key), args, ctx),
			MethodBase m => BuildMethod(m, args, targs, ctx, out errResult),
			_ => throw new KernPanic($"неподдерживаемый нативный член: {key}"),
		};
		var core = Expression.Lambda<Core>(body, vm, args, targs).Compile();
		var bound = new Bound(core, key, errResult);
		return bound.Invoke;
	}

	/// <summary>Исполнитель: вызывает core и переводит исключения C# в KernPanic (или Err для KResult).</summary>
	private sealed class Bound(Core core, string key, bool errResult)
	{
		// Span нельзя захватывать в Expression, поэтому копируем аргументы в массив.
		public KValue Invoke(Interpreter vm, ReadOnlySpan<KValue> args, KType[] typeArgs)
		{
			try { return core(vm, args.ToArray(), typeArgs); }
			catch (Exception e) when (e is KernPanic or KernExit) { throw; }
			catch (Exception e) when (errResult) { return Marshaller.ErrResult(e); }
			catch (Exception e) { throw Marshaller.Wrap(e, key); }
		}
	}

	// ---------- поля ----------

	private static Expression BuildField(FieldInfo f, bool setter, ParameterExpression args, MarshalExpr.Ctx ctx)
	{
		// const-поле: значение подставляется как константа (Expression.Field для literal недопустим)
		if (f.IsLiteral && !setter)
			return Box(Expression.Constant(f.GetRawConstantValue(), f.FieldType), f.FieldType);

		Expression? target = null;
		int ai = 0;
		if (!f.IsStatic) target = MarshalExpr.ToClr(Expression.ArrayIndex(args, Expression.Constant(ai++)), f.DeclaringType!, ctx);
		var field = Expression.Field(target, f);
		if (!setter) return Box(field, f.FieldType);

		var value = MarshalExpr.ToClr(Expression.ArrayIndex(args, Expression.Constant(ai)), f.FieldType, ctx);
		return Expression.Block(Expression.Assign(field, value), Expression.Constant(KValue.Void));
	}

	// ---------- методы и конструкторы ----------

	private static Expression BuildMethod(MethodBase m, ParameterExpression args, ParameterExpression targs, MarshalExpr.Ctx ctx, out bool errResult)
	{
		m = Instantiate(m);
		var ps = m.GetParameters();
		int ai = 0;

		Expression? target = null;
		if (!m.IsStatic && m is not ConstructorInfo)
			target = MarshalExpr.ToClr(Expression.ArrayIndex(args, Expression.Constant(ai++)), m.DeclaringType!, ctx);

		var callArgs = new Expression[ps.Length];
		for (int i = 0; i < ps.Length; i++)
		{
			var p = ps[i];
			var pt = p.ParameterType;
			if (pt.IsByRef) pt = pt.GetElementType()!;
			if (p.IsDefined(typeof(KernTypeArgsAttribute), false) || pt == typeof(KType[])) { callArgs[i] = targs; continue; }
			if (p.IsDefined(typeof(KernVmAttribute), false) || pt == typeof(Interpreter)) { callArgs[i] = ctx.Vm; continue; }

			var arg = Expression.ArrayIndex(args, Expression.Constant(ai));
			Expression conv = MarshalExpr.ToClr(arg, pt, ctx);
			if (p.HasDefaultValue)
			{
				// аргумент опущен (короче args) или передан None → значение по умолчанию C#
				var missing = Expression.OrElse(
					Expression.LessThanOrEqual(Expression.ArrayLength(args), Expression.Constant(ai)),
					Expression.Property(arg, nameof(KValue.IsNone)));
				conv = Expression.Condition(missing, DefaultOf(p, pt), conv);
			}
			callArgs[i] = conv;
			ai++;
		}

		Expression call = m switch
		{
			ConstructorInfo c => Expression.New(c, callArgs),
			MethodInfo mi => Expression.Call(target, mi, callArgs),
			_ => throw new KernPanic($"неподдерживаемый член {m.Name}"),
		};

		var ret = m is MethodInfo r ? r.ReturnType : ((ConstructorInfo)m).DeclaringType!;
		errResult = ret.IsGenericType && ret.GetGenericTypeDefinition() == typeof(KResult<,>);
		if (ret == typeof(void)) return Expression.Block(call, Expression.Constant(KValue.Void));
		if (ret == typeof(Task) || ret == typeof(ValueTask) || (ret.IsGenericType
			&& ret.GetGenericTypeDefinition() is var d && (d == typeof(Task<>) || d == typeof(ValueTask<>))))
			return MarshalExpr.FromTask(call, ret);
		return Box(call, ret);
	}

	/// <summary>CLR-выражение → KValue (значение сначала кладём в переменную — оно может использоваться несколько раз).</summary>
	private static Expression Box(Expression value, Type type)
	{
		var tmp = Expression.Variable(type, "r");
		return Expression.Block([tmp], Expression.Assign(tmp, value), MarshalExpr.FromClr(tmp, type));
	}

	private static Expression DefaultOf(ParameterInfo p, Type pt)
	{
		var v = p.DefaultValue;
		if (v is null || v is DBNull || v is Missing) return Expression.Default(pt);
		if (pt.IsEnum) return Expression.Constant(Enum.ToObject(pt, v), pt);
		var c = Expression.Constant(v);
		return c.Type == pt ? c : Expression.Convert(c, pt);
	}

	/// <summary>Generic-тип/метод → инстанцирование с T = KValue (все фантомные параметры стираются).</summary>
	private static MethodBase Instantiate(MethodBase m)
	{
		try
		{
			var decl = m.DeclaringType!;
			if (decl.IsGenericTypeDefinition)
			{
				var closed = decl.MakeGenericType(Enumerable.Repeat(typeof(KValue), decl.GetGenericArguments().Length).ToArray());
				m = MethodBase.GetMethodFromHandle(m.MethodHandle, closed.TypeHandle)!;
			}
			if (m is MethodInfo { IsGenericMethodDefinition: true } mi)
				m = mi.MakeGenericMethod(Enumerable.Repeat(typeof(KValue), mi.GetGenericArguments().Length).ToArray());
			return m;
		}
		catch (ArgumentException e)
		{
			throw new KernPanic($"нельзя инстанцировать {m.Name} с T=KValue (используйте [KernConstraint] вместо where): {e.Message}", e);
		}
	}
}
