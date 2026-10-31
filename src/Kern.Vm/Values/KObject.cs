using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;

namespace Kern.Vm.Values;

/// <summary>
/// Экземпляр объявленного <c>type</c>/<c>unit</c>: reified-тип и слоты полей.
/// Доступ к полям идёт только через <see cref="GetField"/>/<see cref="SetField"/>:
/// прямая запись в массив обошла бы проверку неизменяемости <c>unit!</c> (spec §9).
/// </summary>
public sealed class KObject
{
	private readonly KValue[] _fields;

	/// <summary>
	/// Глубина открытых окон создания: пока конструктор типа выполняется, поля
	/// неизменяемого unit ещё можно заполнять (spec §8: <c>init</c> — во время создания;
	/// spec §9: менять состояние <c>unit!</c> нельзя лишь ПОСЛЕ создания). Счётчик,
	/// а не флаг: цепочка конструкторов <c>: $(...)</c> открывает окно несколько раз.
	/// </summary>
	private int _constructionDepth;

	public KObject(KClassType type, KValue[] fields)
		: this(type, fields, type.Definition.Kind is TypeDefKind.Unit or TypeDefKind.ImmutableUnit)
	{
	}

	/// <param name="isUnit">Явный флаг нужен для синтетических объектов, у которых нет <see cref="TypeDef"/>-unit.</param>
	public KObject(KClassType type, KValue[] fields, bool isUnit)
	{
		Type = type ?? throw new ArgumentNullException(nameof(type));
		_fields = fields ?? throw new ArgumentNullException(nameof(fields));
		IsUnit = isUnit;
	}

	public KClassType Type { get; }

	/// <summary>Значение-тип: копируется опкодом <c>CopyUnit</c> при присваивании/передаче.</summary>
	public bool IsUnit { get; }

	/// <summary><c>unit!</c>: поля нельзя менять после создания.</summary>
	public bool IsImmutable => Type.Definition.Kind == TypeDefKind.ImmutableUnit;

	public int FieldCount => _fields.Length;

	/// <summary>Только чтение: слоты видны снаружи, но менять их можно лишь через <see cref="SetField"/>.</summary>
	public ReadOnlySpan<KValue> Fields => _fields;

	public KValue GetField(int slot)
	{
		if ((uint)slot >= (uint)_fields.Length)
			throw new KernPanic($"поле {slot} вне объекта типа '{Type.Canonical}': полей {_fields.Length}");
		return _fields[slot];
	}

	public void SetField(int slot, KValue value)
	{
		if ((uint)slot >= (uint)_fields.Length)
			throw new KernPanic($"поле {slot} вне объекта типа '{Type.Canonical}': полей {_fields.Length}");
		if (IsImmutable && _constructionDepth == 0)
			throw new KernPanic($"нельзя изменить поле {slot} неизменяемого unit-типа '{Type.Canonical}'");
		_fields[slot] = value;
	}

	/// <summary>
	/// Открыть окно создания: вызывается VM на входе в конструктор этого объекта.
	/// Пока окно открыто, поля неизменяемого unit доступны для записи (пролог, <c>init</c>,
	/// тело конструктора, цепочка конструкторов).
	/// </summary>
	public void BeginConstruction() => _constructionDepth++;

	/// <summary>
	/// Закрыть окно создания: после возврата из внешнего конструктора объект запечатан.
	/// Парность проверяется громко: непарное закрытие — ошибка VM, а не повод оставить
	/// объект записываемым навсегда.
	/// </summary>
	public void EndConstruction()
	{
		if (_constructionDepth <= 0)
			throw new KernPanic(
				$"закрытие окна создания без открытия у '{Type.Canonical}': конструктор не вызывался");
		_constructionDepth--;
	}

	/// <summary>
	/// Копия значения unit: вложенные unit копируются рекурсивно, list/dict/прочие объекты
	/// остаются по ссылке (spec §9). У неизменяемого unit копия тоже неизменяема.
	/// </summary>
	public KObject CopyUnit() => CloneShallow();

	/// <summary>
	/// B8c: неглубокая копия объекта для <c>#[derive(clone)]</c>. Поля-значения
	/// (<c>unit</c>/<c>unit!</c>) копируются рекурсивно, ссылки (<c>type</c>, коллекции,
	/// замыкания) остаются общими; reified-тип и признак значения сохраняются.
	/// </summary>
	public KObject CloneShallow()
	{
		KValue[] fields = new KValue[_fields.Length];
		for (int i = 0; i < _fields.Length; i++)
		{
			KValue field = _fields[i];
			fields[i] = field.Ref is KObject nested && nested.IsUnit
				? KValue.Obj(nested.CloneShallow())
				: field;
		}
		return new KObject(Type, fields, IsUnit);
	}

	public override string ToString() => $"{Type.Canonical} #{GetHashCode():x}";
}
