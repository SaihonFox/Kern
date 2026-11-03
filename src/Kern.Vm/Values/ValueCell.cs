namespace Kern.Vm.Values;

/// <summary>
/// Ячейка на место хранения значения (spec §6): переменная кадра, поле объекта или
/// статическое поле модуля. Используется <c>*T</c>-параметрами: вызываемый читает и
/// пишет по месту вызывающего.
/// </summary>
public interface IValueCell
{
	KValue Get();

	void Set(KValue value);
}

/// <summary>Ячейка на слот массива локальных переменных кадра.</summary>
public sealed class LocalValueCell(KValue[] locals, int slot) : IValueCell
{
	public KValue Get() => locals[slot];

	public void Set(KValue value) => locals[slot] = value;
}

/// <summary>Ячейка на слот статического поля модуля.</summary>
public sealed class StaticValueCell(KValue[] statics, int slot) : IValueCell
{
	public KValue Get() => statics[slot];

	public void Set(KValue value) => statics[slot] = value;
}

/// <summary>Ячейка на поле объекта (unit/type).</summary>
public sealed class FieldValueCell(KObject target, int slot) : IValueCell
{
	public KValue Get() => target.GetField(slot);

	public void Set(KValue value) => target.SetField(slot, value);
}
