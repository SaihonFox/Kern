using System.Numerics;
using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;
using Kern.Binding.Wrappers;

namespace Kern.Binding.Marshalling;

/// <summary>
/// Рантайм-хелперы маршалинга KValue ↔ CLR. Вызываются из скомпилированных Expression-деревьев (<see cref="MarshalExpr"/>),
/// поэтому имена методов уникальны (без перегрузок) — их ищут по имени.
/// </summary>
public static class Marshaller
{
	public static string Describe(KValue v) => v.Kind switch
	{
		ValueKind.Ref => v.Ref?.GetType().Name ?? "null",
		ValueKind.Str => "str",
		_ => v.Kind.ToString().ToLowerInvariant(),
	};

	public static KernPanic Mismatch(KValue v, string want) => new($"ожидалось {want}, получено {Describe(v)}");

	// ---------- KValue → CLR ----------

	public static bool IsNil(KValue v) => v.Kind is ValueKind.Nil or ValueKind.None;

	public static bool ToBool(KValue v) => v.Kind == ValueKind.Bool ? v.Bits != 0 : throw Mismatch(v, "bool");

	/// <summary>Любое целое/char/bool → long; float усекается. Дальше выражение делает unchecked-Convert к нужной ширине.</summary>
	public static long ToI64(KValue v) => v.Kind switch
	{
		>= ValueKind.I8 and <= ValueKind.U64 or ValueKind.Char or ValueKind.Bool => v.Bits,
		ValueKind.F16 or ValueKind.F32 or ValueKind.F64 => (long)v.AsF64,
		_ => throw Mismatch(v, "целое число"),
	};

	public static char ToChar(KValue v) => v.Kind switch
	{
		ValueKind.Char => (char)v.Bits,
		ValueKind.Str when v.AsStr.Length == 1 => v.AsStr[0],
		_ when v.IsInteger => (char)v.Bits,
		_ => throw Mismatch(v, "char"),
	};

	public static double ToF64(KValue v) => v.IsNumber ? v.AsF64 : v.Kind == ValueKind.Char ? v.Bits : throw Mismatch(v, "число");

	public static string? ToStr(KValue v) => v.Kind switch
	{
		ValueKind.Str => v.AsStr,
		ValueKind.Nil or ValueKind.None => null,
		ValueKind.Char => ((char)v.Bits).ToString(),
		_ => throw Mismatch(v, "str"),
	};

	public static decimal ToDecimal(KValue v) => v switch
	{
		{ Ref: KNativeBox { Value: decimal d } } => d,
		{ Ref: KNativeBox { Value: BigInteger b } } => (decimal)b,
		{ IsInteger: true } => v.IsUnsigned ? (decimal)v.AsU64 : v.Bits,
		{ IsFloat: true } => (decimal)v.AsF64,
		_ => throw Mismatch(v, "decimal"),
	};

	public static BigInteger ToBigInt(KValue v) => v switch
	{
		{ Ref: KNativeBox { Value: BigInteger b } } => b,
		{ Ref: KNativeBox { Value: decimal d } } => new BigInteger(d),
		{ IsInteger: true } => v.IsUnsigned ? (BigInteger)v.AsU64 : v.Bits,
		{ IsFloat: true } => new BigInteger(v.AsF64),
		_ => throw Mismatch(v, "bigint"),
	};

	/// <summary>Значение C#-enum: Kern-enum (KObject, поле 0) либо просто целое.</summary>
	public static long EnumBits(KValue v) => v.Ref is KObject { Fields.Length: > 0 } o ? o.Fields[0].Bits : ToI64(v);

	public static KType ToKType(KValue v) => v.Ref switch
	{
		KTypeObject t => t.Type,
		_ => throw Mismatch(v, "type"),
	};

	public static KFunc.Callable? MakeCallable(Interpreter vm, KValue v) => IsNil(v) ? null : new KFunc.Callable(vm, v);

	/// <summary>Внутренний VM-объект обёртки (KListObject, KDictObject, KResultObject…).</summary>
	public static T RefOf<T>(KValue v) where T : class => v.Ref as T ?? throw Mismatch(v, typeof(T).Name);

	public static object? ToObject(KValue v) => Num.ToClr(v);

	/// <summary>Ссылочный CLR-тип: KNativeBox → значение, уже готовый VM/CLR-объект, либо нативная часть KObject.</summary>
	public static T? ClassOf<T>(KValue v)
	{
		if (IsNil(v)) return default;
		var r = v.Ref;
		if (r is T t) return t;
		if (r is KNativeBox { Value: T bt }) return bt;
		if (r is KObject { Native: { } n }) { if (n is T nt) return nt; if (n is KNativeBox { Value: T nbt }) return nbt; }
		if (NativeAdapters.Adapt(v, typeof(T)) is T adapted) return adapted;   // list/array/str/dict/range → collection<T> и т.п.
		throw Mismatch(v, typeof(T).Name);
	}

	public static T StructOf<T>(KValue v) where T : struct
	{
		var r = v.Ref;
		if (r is T t) return t;
		if (r is KNativeBox { Value: T bt }) return bt;
		throw Mismatch(v, typeof(T).Name);
	}

	/// <summary>Kern list/array/set → List&lt;T&gt; с поэлементным преобразованием.</summary>
	public static List<T> ToSeq<T>(KValue v, Func<KValue, T> conv)
	{
		IReadOnlyList<KValue> src = v.Ref switch
		{
			KListObject l => l.Items,
			KArrayObject a => a.Items,
			KTupleObject t => t.Items,
			_ => throw Mismatch(v, "list/array"),
		};
		var res = new List<T>(src.Count);
		for (int i = 0; i < src.Count; i++) res.Add(conv(src[i]));
		return res;
	}

	// ---------- CLR → KValue ----------

	public static KValue FromStr(string? s) => s is null ? KValue.Nil : KValue.Str(s);
	public static KValue FromKType(KType? t) => t is null ? KValue.Nil : KValue.Obj(new KTypeObject(t));
	public static KValue FromBigInt(BigInteger b) => KValue.Obj(new KNativeBox(b));
	public static KValue FromDecimal(decimal d) => KValue.Obj(new KNativeBox(d));
	public static KValue FromObject(object? o) => o is IValueAdapter ad ? ad.Original : Num.FromClr(o);

	public static KValue FromSeq<T>(IEnumerable<T>? src, Func<T, KValue> conv)
	{
		if (src is null) return KValue.Nil;
		var list = new KListObject(TypeFactory.Object);
		foreach (var x in src) list.Items.Add(conv(x));
		return KValue.Obj(list);
	}

	public static KValue FromTask(Task t) => KValue.Obj(KTask.FromDotnet(t));
	public static KValue FromTaskOf<T>(Task<T> t, Func<T, KValue> conv) => KValue.Obj(KTask.FromDotnet(t, conv));
	public static KValue FromValueTask(ValueTask t) => KValue.Obj(KTask.FromValueTask(t));
	public static KValue FromValueTaskOf<T>(ValueTask<T> t, Func<T, KValue> conv) => KValue.Obj(KTask.FromValueTask(t, conv));

	// ---------- ошибки ----------

	public static KValue ErrResult(Exception e) => KValue.Obj(new KResultObject(false, KValue.Str(e.Message)));

	public static KernPanic Wrap(Exception e, string member) => e switch
	{
		KernPanic p => p,
		_ => new KernPanic($"{Describe(e)} (в {member})", e),
	};

	/// <summary>Сообщение с цепочкой внутренних исключений: «The type initializer … threw» без причины бесполезно.</summary>
	private static string Describe(Exception e)
	{
		var parts = new List<string> { e.Message };
		for (var inner = e.InnerException; inner is not null && parts.Count < 4; inner = inner.InnerException)
			parts.Add($"{inner.GetType().Name}: {inner.Message}");
		return string.Join(" ← ", parts);
	}
}
