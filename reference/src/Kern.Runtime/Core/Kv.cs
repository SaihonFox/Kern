using Kern.Binding.Wrappers;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Core;

/// <summary>Внутренние помощники для нативных API (не привязываются к Kern): создание списков, Ok/Err, сравнение.</summary>
public static class Kv
{
	public static KType Obj => TypeFactory.Object;
	public static KType Str => TypeFactory.Str;

	public static KList<T> List<T>(KType elem, IEnumerable<KValue> items) => new(new KListObject(elem, [.. items]));
	public static KList<T> List<T>(IEnumerable<KValue> items) => List<T>(Obj, items);
	public static KList<string> Strs(IEnumerable<string> items) => List<string>(Str, items.Select(KValue.Str));
	public static KList<char> Chars(IEnumerable<char> items) => List<char>(TypeFactory.Prim(Kern.Bytecode.PrimKind.Char), items.Select(KValue.Char));

	public static KResult<T, string> Ok<T>(KValue v) => KResult<T, string>.Ok(v);
	public static KResult<T, string> Err<T>(string message) => KResult<T, string>.Err(KValue.Str(message));

	public static KTupleObject Tuple(params KValue[] items) => new(items);

	/// <summary>Сравнение значений для sort/min/max: числа, строки, char; иначе по ToStr.</summary>
	public static int Compare(KValue a, KValue b)
	{
		if (a.IsNumber && b.IsNumber) { int c = Num.Compare(a, b); return c == 2 ? 0 : c; }
		if (a.Kind == ValueKind.Str && b.Kind == ValueKind.Str) return string.CompareOrdinal(a.AsStr, b.AsStr);
		if (a.Kind == ValueKind.Char && b.Kind == ValueKind.Char) return a.Bits.CompareTo(b.Bits);
		if (a.Kind == ValueKind.Bool && b.Kind == ValueKind.Bool) return a.Bits.CompareTo(b.Bits);
		return string.CompareOrdinal(ValueOps.ToStr(a), ValueOps.ToStr(b));
	}

	public static readonly Comparison<KValue> Cmp = Compare;

	/// <summary>T ↔ KValue: в рантайме generic-параметры нативов всегда равны KValue, переинтерпретация без бокса.</summary>
	public static T As<T>(KValue v) => System.Runtime.CompilerServices.Unsafe.As<KValue, T>(ref v);
	public static KValue Of<T>(T v) => System.Runtime.CompilerServices.Unsafe.As<T, KValue>(ref v);

	/// <summary>Reified-аргумент результата: последний из переданных, иначе object.</summary>
	public static KType Last(KType[] typeArgs) => typeArgs.Length > 0 ? typeArgs[^1] : Obj;

	public static KValue Nil => KValue.Nil;

	public static bool Eq(KValue a, KValue b) => ValueOps.StructuralEquals(a, b);

	public static bool Truthy(KValue v) => v.Kind == ValueKind.Bool ? v.Bits != 0 : throw new Kern.Vm.Execution.KernPanic("предикат должен вернуть bool");
}
