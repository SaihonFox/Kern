using Kern.Vm.Execution;

namespace Kern.Vm.Values;

/// <summary>
/// Ячейка для захвата переменной по ссылке (замыкания с изменяемым состоянием). Живёт
/// как ссылочное значение; <c>LoadCell</c>/<c>StoreCell</c> читают и пишут <see cref="Value"/>.
/// </summary>
public sealed class KCell(KValue value) : IValueCell
{
	public KValue Value { get; set; } = value;

	public KValue Get() => Value;

	public void Set(KValue value) => Value = value;

	public override string ToString() => $"cell({Value.TypeName})";

	internal static KCell Require(KValue value, string operation) =>
		value.Ref as KCell ?? throw new KernPanic($"{operation}: ожидалась ячейка, получено {value.TypeName}");
}
