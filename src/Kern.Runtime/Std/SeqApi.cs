using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Std;

/// <summary>
/// Ленивая последовательность <c>seq&lt;T&gt;</c> (spec §6, раунд 27). Генератор создаёт
/// её вызовом функции с <c>yield</c>; обход — <c>loop</c> (через <c>iter&lt;T&gt;</c>) или
/// <c>next()</c>. Цепочки <c>map/filter/take/skip</c> ленивые и не материализуют источник.
/// </summary>
[KernType("seq", GenericArity = 1, Parent = "iter<T>")]
public static class SeqApi
{
	/// <summary><c>next() -&gt; T?</c>: следующий элемент или <c>nil</c> в конце.</summary>
	[KernMember]
	[KernName("next")]
	[KernReturn("T?")]
	public static KValue Next(KNativeBox self) =>
		Require(self).TryNext(out KValue value) ? value : KValue.Nil;

	/// <summary><c>iter() -&gt; seq&lt;T&gt;</c>: независимый курсор для повторного обхода.</summary>
	[KernMember]
	[KernName("iter")]
	[KernReturn("seq<T>")]
	public static KValue Iter(KNativeBox self) =>
		KValue.Obj(new KNativeBox(Require(self).Iter()));

	/// <summary><c>len</c>: число элементов; дочитывает последовательность до конца.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Len;

	/// <summary><c>to_list() -&gt; list&lt;T&gt;</c>: материализовать последовательность с начала.</summary>
	[KernMember]
	[KernName("to_list")]
	[KernReturn("list<T>")]
	public static KValue ToList(KNativeBox self)
	{
		var items = new List<KValue>();
		KSeq view = Require(self).Iter();
		while (view.TryNext(out KValue item))
		{
			if (items.Count == int.MaxValue)
				throw new KernPanic("seq.to_list: слишком длинная последовательность");
			items.Add(item);
		}

		return KValue.Obj(new KList(null, items));
	}

	/// <summary><c>map&lt;U&gt;(fun(T) -&gt; U) -&gt; seq&lt;U&gt;</c>: ленивое преобразование.</summary>
	[KernMember]
	[KernName("map")]
	[KernReturn("seq<U>")]
	public static KValue Map<U>(
		Interpreter interpreter,
		KNativeBox self,
		[KernParameterType("fun(T)->U")] KValue function) =>
		KValue.Obj(new KNativeBox(new KSeq(new MapSource(interpreter, Require(self).Iter(), function))));

	/// <summary><c>filter(fun(T) -&gt; bool) -&gt; seq&lt;T&gt;</c>: ленивый отбор.</summary>
	[KernMember]
	[KernName("filter")]
	[KernReturn("seq<T>")]
	public static KValue Filter(
		Interpreter interpreter,
		KNativeBox self,
		[KernParameterType("fun(T)->bool")] KValue function) =>
		KValue.Obj(new KNativeBox(new KSeq(new FilterSource(interpreter, Require(self).Iter(), function))));

	/// <summary><c>take(n) -&gt; seq&lt;T&gt;</c>: первые <c>n</c> элементов.</summary>
	[KernMember]
	[KernReturn("seq<T>")]
	public static KValue Take(KNativeBox self, int count) =>
		KValue.Obj(new KNativeBox(new KSeq(new TakeSource(Require(self).Iter(), count))));

	/// <summary><c>skip(n) -&gt; seq&lt;T&gt;</c>: пропустить первые <c>n</c> элементов.</summary>
	[KernMember]
	[KernReturn("seq<T>")]
	public static KValue Skip(KNativeBox self, int count) =>
		KValue.Obj(new KNativeBox(new KSeq(new SkipSource(Require(self).Iter(), count))));

	private static KSeq Require(KNativeBox self) => self.Value switch
	{
		KSeq sequence => sequence,
		_ => throw new KernPanic($"seq: ожидалась ленивая последовательность, получено {self.Value.GetType().Name}"),
	};

	private sealed class MapSource(Interpreter interpreter, KSeq source, KValue function) : ISeqSource
	{
		public bool TryNext(out KValue value)
		{
			if (!source.TryNext(out KValue item))
			{
				value = KValue.None;
				return false;
			}

			value = interpreter.RunValue(function, item);
			return true;
		}
	}

	private sealed class FilterSource(Interpreter interpreter, KSeq source, KValue function) : ISeqSource
	{
		public bool TryNext(out KValue value)
		{
			while (source.TryNext(out KValue item))
			{
				KValue keep = interpreter.RunValue(function, item);
				if (keep.Kind != ValueKind.Bool)
					throw new KernPanic($"seq.filter: предикат вернул {keep.TypeName}, ожидался bool");
				if (keep.AsBool)
				{
					value = item;
					return true;
				}
			}

			value = KValue.None;
			return false;
		}
	}

	private sealed class TakeSource(KSeq source, int count) : ISeqSource
	{
		private int _remaining = System.Math.Max(0, count);

		public bool TryNext(out KValue value)
		{
			if (_remaining <= 0)
			{
				value = KValue.None;
				return false;
			}

			if (!source.TryNext(out value))
			{
				_remaining = 0;
				return false;
			}

			_remaining--;
			return true;
		}
	}

	private sealed class SkipSource(KSeq source, int count) : ISeqSource
	{
		private int _remaining = System.Math.Max(0, count);

		public bool TryNext(out KValue value)
		{
			while (_remaining > 0)
			{
				if (!source.TryNext(out _))
				{
					_remaining = 0;
					value = KValue.None;
					return false;
				}

				_remaining--;
			}

			return source.TryNext(out value);
		}
	}
}
