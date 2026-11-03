using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>Локальная переменная или параметр с выделенным слотом.</summary>
internal sealed class BoundVariable(string name, KernType type, int slot, bool isSingleAssignment)
{
	public string Name { get; } = name;

	public KernType Type { get; set; } = type;

	public int Slot { get; } = slot;

	/// <summary>Флаг <c>var!</c>/<c>T!</c>: значение присваивается ровно один раз.</summary>
	public bool IsSingleAssignment { get; } = isSingleAssignment;

	public bool IsParameter { get; init; }

	/// <summary>Способ передачи параметра (spec §6); у локальных переменных — <c>Value</c>.</summary>
	public ParameterPassing Passing { get; init; }

	/// <summary>
	/// Идентификатор кадра (функции), в котором объявлена переменная. Слоты уникальны
	/// только внутри кадра, поэтому чтение из другого кадра — захват замыкания.
	/// </summary>
	public int Frame { get; init; }
}

/// <summary>
/// Цепочка областей видимости функции. Поиск идёт от текущей области к внешним (замыкания
/// получат общий слот через родителя; в слайсе A замыканий нет).
/// </summary>
internal sealed class BoundScope(BoundScope? parent)
{
	private readonly Dictionary<string, BoundVariable> _variables = new(StringComparer.Ordinal);

	public BoundScope? Parent { get; } = parent;

	public bool TryDeclare(BoundVariable variable) => _variables.TryAdd(variable.Name, variable);

	public BoundVariable? Lookup(string name)
	{
		for (var scope = this; scope is not null; scope = scope.Parent)
			if (scope._variables.TryGetValue(name, out var variable))
				return variable;

		return null;
	}
}
