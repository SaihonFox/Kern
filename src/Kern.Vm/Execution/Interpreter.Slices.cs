using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>Срезы списков/массивов/строк и их нормализация (spec §3, раунд 27).</summary>
public sealed partial class Interpreter
{
	/// <summary>
	/// B4b2: копия диапазона <c>start..end</c> (конец не включён) из коллекции длины
	/// <paramref name="count"/>. Некорректный диапазон — паника.
	/// </summary>
	private static KValue[] SliceItems(int count, Func<int, KValue> itemAt, int start, int end)
	{
		if (start < 0 || end < start || end > count)
			throw new KernPanic($"ListSlice: диапазон {start}..{end} вне длины {count}");
		KValue[] items = new KValue[end - start];
		for (int i = start; i < end; i++)
			items[i - start] = itemAt(i);
		return items;
	}

	/// <summary>
	/// B5b2: границы среза <c>start..end</c> (spec round 27): отрицательные считаются с конца,
	/// включительный конец увеличивается на 1. Некорректный диапазон — паника.
	/// </summary>
	private static (int Start, int End) NormalizeSlice(int start, int end, bool inclusive, int count)
	{
		int from = NormalizeIndex(start, count);
		int to = NormalizeIndex(end, count) + (inclusive ? 1 : 0);
		if (from < 0 || to < from || to > count)
			throw new KernPanic($"Slice: диапазон {start}..{end} вне длины {count}");
		return (from, to);
	}

	/// <summary>
	/// B5b2: срез <c>start..end</c> из <c>list</c>/<c>array</c>/<c>str</c>. Стек:
	/// <c>[receiver, start, end]</c>; <paramref name="inclusive"/> — включительный конец.
	/// Отрицательные границы считаются с конца, затем диапазон проверяется (паника)
	/// и копируется в новый list/str.
	/// </summary>
	private static void Slice(ref OperandStack stack, bool inclusive)
	{
		KValue endValue = stack.Pop();
		KValue startValue = stack.Pop();
		KValue receiver = stack.Pop();
		if (startValue.Kind != ValueKind.I32)
			throw new KernPanic($"Slice: ожидалось начало i32, получено {startValue.TypeName}");
		if (endValue.Kind != ValueKind.I32)
			throw new KernPanic($"Slice: ожидался конец i32, получено {endValue.TypeName}");
		int start = startValue.AsI32;
		int end = endValue.AsI32;
		if (receiver.Kind == ValueKind.Str)
		{
			string text = receiver.AsStr;
			(int from, int to) = NormalizeSlice(start, end, inclusive, text.Length);
			stack.Push(KValue.Str(text.Substring(from, to - from)));
			return;
		}

		switch (receiver.Ref)
		{
			case KList list:
				{
					(int from, int to) = NormalizeSlice(start, end, inclusive, list.Count);
					stack.Push(KValue.Obj(new KList(
						list.ElementType,
						SliceItems(list.Count, index => list[index], from, to))));
					break;
				}
			case KArray array:
				{
					(int from, int to) = NormalizeSlice(start, end, inclusive, array.Length);
					stack.Push(KValue.Obj(new KList(
						array.ElementType,
						SliceItems(array.Length, index => array[index], from, to))));
					break;
				}
			default:
				throw new KernPanic(
					$"Slice: ожидался list, array или str, получено {receiver.TypeName}");
		}
	}

	/// <summary>
	/// Открытый срез <c>xs[start..]</c>/<c>xs[start..:step]</c>: конец — длина коллекции.
	/// Стек: <c>[receiver, start]</c> или <c>[receiver, start, step]</c>.
	/// </summary>
	private static void SliceToEnd(ref OperandStack stack, bool withStep)
	{
		int step = 1;
		if (withStep)
		{
			KValue stepValue = stack.Pop();
			if (stepValue.Kind != ValueKind.I32)
				throw new KernPanic($"Slice: ожидался шаг i32, получено {stepValue.TypeName}");
			step = stepValue.AsI32;
		}

		KValue startValue = stack.Pop();
		KValue receiver = stack.Pop();
		if (startValue.Kind != ValueKind.I32)
			throw new KernPanic($"Slice: ожидалось начало i32, получено {startValue.TypeName}");
		stack.Push(SliceToEnd(receiver, startValue.AsI32, step));
	}

	private static KValue SliceToEnd(KValue receiver, int start, int step)
	{
		int count = receiver.Kind == ValueKind.Str
			? receiver.AsStr.Length
			: receiver.Ref switch
			{
				KList list => list.Count,
				KArray array => array.Length,
				_ => throw new KernPanic(
					$"Slice: ожидался list, array или str, получено {receiver.TypeName}")
			};
		return SliceWithStep(receiver, start, count, inclusive: false, step);
	}

	/// <summary>Снять <c>[receiver, start, end, step]</c> и положить срез с шагом.</summary>
	private static void SliceStep(ref OperandStack stack, bool inclusive)
	{
		KValue stepValue = stack.Pop();
		KValue endValue = stack.Pop();
		KValue startValue = stack.Pop();
		KValue receiver = stack.Pop();
		if (stepValue.Kind != ValueKind.I32 || endValue.Kind != ValueKind.I32 ||
			startValue.Kind != ValueKind.I32)
			throw new KernPanic("SliceStep: ожидались индексы и шаг i32");
		stack.Push(SliceWithStep(
			receiver,
			startValue.AsI32,
			endValue.AsI32,
			inclusive,
			stepValue.AsI32));
	}

	/// <summary>
	/// Срез с шагом (spec §3): <c>0</c> и отрицательный шаг запрещены, границы
	/// нормализуются как у обычного среза, индексы идут от <c>from</c> к <c>to</c>.
	/// </summary>
	private static KValue SliceWithStep(KValue receiver, int start, int end, bool inclusive, int step)
	{
		if (step <= 0)
			throw new KernPanic($"Slice: шаг должен быть положительным, получено {step}");
		if (receiver.Kind == ValueKind.Str)
		{
			string text = receiver.AsStr;
			(int from, int to) = NormalizeSlice(start, end, inclusive, text.Length);
			var builder = new System.Text.StringBuilder((to - from + step - 1) / step);
			for (int index = from; index < to; index += step)
				builder.Append(text[index]);
			return KValue.Str(builder.ToString());
		}

		switch (receiver.Ref)
		{
			case KList list:
				{
					(int from, int to) = NormalizeSlice(start, end, inclusive, list.Count);
					var items = new List<KValue>((to - from + step - 1) / step);
					for (int index = from; index < to; index += step)
						items.Add(list[index]);
					return KValue.Obj(new KList(list.ElementType, items));
				}
			case KArray array:
				{
					(int from, int to) = NormalizeSlice(start, end, inclusive, array.Length);
					var items = new List<KValue>((to - from + step - 1) / step);
					for (int index = from; index < to; index += step)
						items.Add(array[index]);
					return KValue.Obj(new KList(array.ElementType, items));
				}
			default:
				throw new KernPanic(
					$"Slice: ожидался list, array или str, получено {receiver.TypeName}");
		}
	}
}
