using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;

namespace Kern.Compiler.Semantics.Binding.Flow;

/// <summary>
/// Состояние потока управления в точке программы: достижимость, суженные типы переменных
/// (smart-cast), определённо присвоенные слоты и слоты, присвоенные хотя бы на одном пути
/// (для single-assignment <c>!</c>). Ключ — номер слота: слоты уникальны в функции, а
/// <see cref="Bound.BoundVariableExpression"/> хранит именно слот.
/// Мутабельный объект: для ветвления используйте <see cref="Clone"/>, для слияния — <see cref="Join"/>.
/// </summary>
internal sealed class FlowState
{
	private Dictionary<int, KernType> _narrowed = [];
	private HashSet<int> _assigned = [];
	private HashSet<int> _maybeAssigned = [];

	public bool Reachable { get; private set; } = true;

	public FlowState Clone() => new()
	{
		Reachable = Reachable,
		_narrowed = new Dictionary<int, KernType>(_narrowed),
		_assigned = [.. _assigned],
		_maybeAssigned = [.. _maybeAssigned]
	};

	public void MarkUnreachable() => Reachable = false;

	// ---------------------------------------------------------------- сужение типов

	public KernType? NarrowedType(int slot) => _narrowed.GetValueOrDefault(slot);

	public void Narrow(int slot, KernType type) => _narrowed[slot] = type;

	public void ClearNarrowing(int slot) => _narrowed.Remove(slot);

	/// <summary>Сбрасывает все сужения: на входе в цикл обратное ребро может сделать их неверными.</summary>
	public void ClearNarrowing() => _narrowed.Clear();

	// ---------------------------------------------------------------- присваивание

	public bool IsAssigned(int slot) => _assigned.Contains(slot);

	public bool IsMaybeAssigned(int slot) => _maybeAssigned.Contains(slot);

	public void MarkAssigned(int slot)
	{
		_assigned.Add(slot);
		_maybeAssigned.Add(slot);
	}

	/// <summary>Слот объявлен заново: присвоенность и сужение сбрасываются.</summary>
	public void Reset(int slot)
	{
		_assigned.Remove(slot);
		_maybeAssigned.Remove(slot);
		_narrowed.Remove(slot);
	}

	// ---------------------------------------------------------------- слияние

	/// <summary>
	/// Слияние двух путей. Сужение остаётся, только если оно есть на обоих путях
	/// (при разных типах берётся более широкий); определённо присвоено — пересечение,
	/// присвоено хотя бы где-то — объединение. Недостижимый путь — нейтральный элемент.
	/// </summary>
	public static FlowState Join(FlowState left, FlowState right)
	{
		if (!left.Reachable)
			return right.Clone();
		if (!right.Reachable)
			return left.Clone();

		var result = new FlowState();
		foreach (var (slot, leftType) in left._narrowed)
		{
			if (!right._narrowed.TryGetValue(slot, out var rightType))
				continue;
			if (leftType.Equals(rightType))
				result._narrowed[slot] = leftType;
			else if (Conversions.TryClassify(leftType, rightType, out _))
				result._narrowed[slot] = rightType;
			else if (Conversions.TryClassify(rightType, leftType, out _))
				result._narrowed[slot] = leftType;
		}

		result._assigned = [.. left._assigned.Intersect(right._assigned)];
		result._maybeAssigned = [.. left._maybeAssigned.Union(right._maybeAssigned)];
		return result;
	}

	public static FlowState Join(IEnumerable<FlowState> states)
	{
		FlowState? result = null;
		foreach (var state in states)
			result = result is null ? state.Clone() : Join(result, state);

		return result ?? Unreachable();
	}

	/// <summary>Слияние основного пути (выход из цикла) с путями <c>stop</c>.</summary>
	public static FlowState Join(FlowState first, IEnumerable<FlowState> rest)
	{
		var result = first.Clone();
		foreach (var state in rest)
			result = Join(result, state);

		return result;
	}

	public static FlowState Unreachable()
	{
		var state = new FlowState();
		state.MarkUnreachable();
		return state;
	}
}
