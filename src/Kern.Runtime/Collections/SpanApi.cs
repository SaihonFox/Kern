using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>
/// Изменяемый срез списка <c>span&lt;T&gt;</c> (spec §16): ссылается на элементы исходного
/// <c>list&lt;T&gt;</c>, ничего не копируя. <c>set</c> меняет исходный список; проверка
/// stack-only намеренно не делается (spec §16).
/// </summary>
[KernType("span", GenericArity = 1)]
public sealed class KernSpan
{
	private readonly KList _source;
	private readonly int _start;
	private readonly int _length;

	/// <summary>Срез <c>[start, start + length)</c> списка; границы проверяются.</summary>
	public KernSpan(KValue source, int start, int length)
	{
		_source = CollectionOps.ToList(source);
		if (start < 0 || length < 0 || start > _source.Count - length)
		{
			throw new KernPanic(
				$"span: недопустимый диапазон start={start}, len={length} для списка из {_source.Count}");
		}

		_start = start;
		_length = length;
	}

	/// <summary>Длина среза.</summary>
	public int Count => _length;

	/// <summary>Элементы среза.</summary>
	public IEnumerable<KValue> Items => Enumerable.Range(0, _length).Select(index => _source[_start + index]);

	/// <summary>Элемент среза; отрицательный индекс считается с конца среза.</summary>
	public KValue Get(int index) => _source[_start + Normalize(index)];

	/// <summary>Записать элемент среза (меняет исходный список).</summary>
	public void Set(int index, KValue value) => _source[_start + Normalize(index)] = value;

	/// <summary>Копия видимых элементов как обычный <c>list&lt;T&gt;</c>.</summary>
	public KList ToList() => new(_source.ElementType, Items);

	private int Normalize(int index)
	{
		int actual = index < 0 ? _length + index : index;
		if ((uint)actual >= (uint)_length)
			throw new KernPanic($"span: индекс {index} вне среза (длина {_length})");
		return actual;
	}

	public override string ToString() => $"span({_length})";
}

/// <summary>
/// Фабрики и члены <c>span&lt;T&gt;</c> (spec §16): <c>span(list)</c> и
/// <c>span(list, start, len)</c>, свойства <c>len</c>/<c>is_empty</c>, <c>get/set/to_list</c>.
/// </summary>
[KernType("span", GenericArity = 1)]
public static class SpanApi
{
	/// <summary><c>span(list)</c>: весь список как изменяемый срез.</summary>
	[KernCtor]
	[KernName("span")]
	[KernReturn("span<T>")]
	public static KValue Create<T>([KernParameterType("list<T>")] KValue list) =>
		KValue.Obj(new KNativeBox(new KernSpan(list, 0, CollectionOps.ToList(list).Count)));

	/// <summary><c>span(list, start, len)</c>: участок списка как изменяемый срез.</summary>
	[KernCtor]
	[KernName("span")]
	[KernReturn("span<T>")]
	public static KValue CreateSlice<T>(
		[KernParameterType("list<T>")] KValue list,
		int start,
		int length) =>
		KValue.Obj(new KNativeBox(new KernSpan(list, start, length)));

	/// <summary><c>len</c>: длина среза.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	/// <summary><c>is_empty</c>: срез пуст.</summary>
	[KernName("is_empty")]
	[KernProperty]
	public static bool IsEmpty(KNativeBox self) => Require(self).Count == 0;

	/// <summary><c>get(i) -&gt; T</c>: элемент среза.</summary>
	[KernMember]
	[KernReturn("T")]
	public static KValue Get(KNativeBox self, int index) => Require(self).Get(index);

	/// <summary><c>set(i, v)</c>: записать элемент среза (виден в источнике).</summary>
	[KernMember]
	public static void Set(KNativeBox self, int index, KValue value) => Require(self).Set(index, value);

	/// <summary><c>to_list() -&gt; list&lt;T&gt;</c>: копия видимых элементов.</summary>
	[KernName("to_list")]
	[KernMember]
	[KernReturn("list<T>")]
	public static KValue ToList(KNativeBox self) => KValue.Obj(Require(self).ToList());

	private static KernSpan Require(KNativeBox self) => self.Value switch
	{
		KernSpan span => span,
		_ => throw new KernPanic($"ожидался span, получено {self.Value.GetType().Name}"),
	};
}
