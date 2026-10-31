using Kern.Bytecode;
using Kern.Vm.Types;

namespace Kern.Vm.Values;

/// <summary>Ячейка захваченной переменной (общая для замыканий и объявившей функции).</summary>
public sealed class KCell(KValue value)
{
	public KValue Value = value;
}

/// <summary>Значение-функция: замыкание, ссылка на метод/функцию, привязанный метод или нативный делегат.</summary>
public sealed class KFunction
{
	public FunctionDef? Def { get; init; }
	public int FuncIndex { get; init; } = -1;
	public KCell[] Captures { get; init; } = [];
	/// <summary>Привязанный $self (BindMethod, лямбда внутри метода).</summary>
	public KValue BoundThis { get; init; }
	public bool HasThis { get; init; }
	public KType[] ClassArgs { get; init; } = [];
	public KType[] MethodArgs { get; init; } = [];
	/// <summary>Нативная реализация (прокси, маршалинг, KFunction.FromDelegate).</summary>
	public Func<KValue[], KValue>? Delegate { get; init; }
	public string DisplayName => Def?.Name ?? "native";

	public static KFunction FromDelegate(Func<KValue[], KValue> f) => new() { Delegate = f };
	public override string ToString() => $"<fun {DisplayName}>";
}

/// <summary>Диапазон a..b, a..=b, a..b:step.</summary>
public sealed class KRange(KValue start, KValue end, KValue step, bool inclusive)
{
	public KValue Start { get; } = start;
	public KValue End { get; } = end;
	public KValue Step { get; } = step;
	public bool Inclusive { get; } = inclusive;
	public bool HasStep => !Step.IsNilOrNone;
	public override string ToString() => $"{ValueOps.ToStr(Start)}{(Inclusive ? "..=" : "..")}{ValueOps.ToStr(End)}" + (HasStep ? $":{ValueOps.ToStr(Step)}" : "");

	public bool Contains(KValue v)
	{
		if (!v.IsNumber && v.Kind != ValueKind.Char) return false;
		int c1 = Num.Compare(v, Start);
		if (c1 == 2 || c1 < 0) return false;
		int c2 = Num.Compare(v, End);
		if (c2 == 2 || (Inclusive ? c2 > 0 : c2 >= 0)) return false;
		if (HasStep && Step.AsI64 != 0) return (v.AsI64 - Start.AsI64) % Step.AsI64 == 0;
		return true;
	}
}
