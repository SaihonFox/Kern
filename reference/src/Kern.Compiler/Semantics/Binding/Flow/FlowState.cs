using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Semantics.Binding.Flow;

/// <summary>
/// Состояние потока управления в точке программы: достижимость, суженные типы переменных (smart-cast),
/// определённо присвоенные переменные и переменные, присвоенные хотя бы на одном пути (для single-assign `!`).
/// Мутабельный объект: для ветвления используйте <see cref="Clone"/>, для слияния путей — <see cref="Join"/>.
/// Недостижимое состояние (после `ret`/`stop`/`next`/`panic`) — нейтральный элемент слияния.
/// </summary>
public sealed class FlowState
{
	private Dictionary<Symbol, TypeSymbol> _narrowed = [];
	private HashSet<Symbol> _assigned = [];
	private HashSet<Symbol> _maybeAssigned = [];

	public bool Reachable { get; private set; } = true;

	public FlowState Clone() => new()
	{
		Reachable = Reachable,
		_narrowed = new Dictionary<Symbol, TypeSymbol>(_narrowed),
		_assigned = [.. _assigned],
		_maybeAssigned = [.. _maybeAssigned],
	};

	public void MarkUnreachable() => Reachable = false;

	// ---------------------------------------------------------------- сужение типов

	public TypeSymbol? NarrowedType(Symbol symbol) => _narrowed.GetValueOrDefault(symbol);

	public void Narrow(Symbol symbol, TypeSymbol type) => _narrowed[symbol] = type;

	public void ClearNarrowing(Symbol symbol) => _narrowed.Remove(symbol);

	/// <summary>Сбрасывает сужение для всех переменных с этими именами (присваивание внутри цикла/лямбды).</summary>
	public void ClearNarrowingByName(IReadOnlySet<string> names)
	{
		foreach (var key in _narrowed.Keys.Where(k => names.Contains(k.Name)).ToList()) _narrowed.Remove(key);
	}

	// ---------------------------------------------------------------- присваивание

	/// <summary>Параметры считаются присвоенными всегда; локальные — после <see cref="MarkAssigned"/>.</summary>
	public bool IsAssigned(Symbol symbol) => symbol is not LocalSymbol || _assigned.Contains(symbol);

	public bool IsMaybeAssigned(Symbol symbol) => symbol is not LocalSymbol || _maybeAssigned.Contains(symbol);

	public void MarkAssigned(Symbol symbol)
	{
		_assigned.Add(symbol);
		_maybeAssigned.Add(symbol);
	}

	/// <summary>Переменная объявлена заново (например, на новой итерации цикла): присвоенность и сужение сбрасываются.</summary>
	public void ResetVariable(Symbol symbol)
	{
		_assigned.Remove(symbol);
		_maybeAssigned.Remove(symbol);
		_narrowed.Remove(symbol);
	}

	// ---------------------------------------------------------------- слияние

	/// <summary>
	/// Слияние двух путей. Сужение остаётся, только если оно есть на обоих путях (при разных типах берётся более широкий);
	/// определённо присвоено — пересечение, присвоено хотя бы где-то — объединение.
	/// </summary>
	public static FlowState Join(FlowState a, FlowState b)
	{
		if (!a.Reachable) return b.Clone();
		if (!b.Reachable) return a.Clone();

		var r = new FlowState { Reachable = true };
		foreach (var (sym, ta) in a._narrowed)
		{
			if (!b._narrowed.TryGetValue(sym, out var tb)) continue;
			if (ta.Equals(tb)) r._narrowed[sym] = ta;
			else if (Conversions.IsImplicit(ta, tb)) r._narrowed[sym] = tb;
			else if (Conversions.IsImplicit(tb, ta)) r._narrowed[sym] = ta;
		}
		r._assigned = [.. a._assigned.Intersect(b._assigned)];
		r._maybeAssigned = [.. a._maybeAssigned.Union(b._maybeAssigned)];
		return r;
	}

	public static FlowState Join(IEnumerable<FlowState> states)
	{
		FlowState? acc = null;
		foreach (var s in states) acc = acc is null ? s.Clone() : Join(acc, s);
		return acc ?? Unreachable();
	}

	public static FlowState Unreachable()
	{
		var s = new FlowState();
		s.MarkUnreachable();
		return s;
	}
}
