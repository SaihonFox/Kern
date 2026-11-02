using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>
/// Индексные операции над встроенными коллекциями (spec §5): вынесены из основного
/// цикла, чтобы <c>Interpreter.RunLoop.cs</c> оставался в пределах 800 строк.
/// </summary>
public sealed partial class Interpreter
{
	/// <summary>Длина <c>list</c>/<c>array</c> как <c>i32</c>.</summary>
	private static void ListLen(ref OperandStack stack)
	{
		KValue receiver = stack.Pop();
		stack.Push(receiver.Ref switch
		{
			KList list => KValue.I32(list.Count),
			KArray array => KValue.I32(array.Length),
			_ => throw new KernPanic($"ListLen: ожидался list или array, получено {receiver.TypeName}")
		});
	}

	/// <summary>Стек <c>[list/array, индекс i32]</c>; отрицательный индекс считается с конца.</summary>
	private static void ListGet(ref OperandStack stack)
	{
		KValue indexValue = stack.Pop();
		KValue receiver = stack.Pop();
		if (indexValue.Kind != ValueKind.I32)
			throw new KernPanic($"ListGet: ожидался индекс i32, получено {indexValue.TypeName}");
		int index = indexValue.AsI32;
		stack.Push(receiver.Ref switch
		{
			KList list => list[NormalizeIndex(index, list.Count)],
			KArray array => array[NormalizeIndex(index, array.Length)],
			_ => throw new KernPanic($"ListGet: ожидался list или array, получено {receiver.TypeName}")
		});
	}

	/// <summary>
	/// Стек <c>[list/array, индекс i32, значение]</c>: запись на месте, на стек ничего
	/// не остаётся; отрицательный индекс считается с конца.
	/// </summary>
	private static void ListSet(ref OperandStack stack)
	{
		KValue value = stack.Pop();
		KValue indexValue = stack.Pop();
		KValue receiver = stack.Pop();
		if (indexValue.Kind != ValueKind.I32)
			throw new KernPanic($"ListSet: ожидался индекс i32, получено {indexValue.TypeName}");
		int index = indexValue.AsI32;
		switch (receiver.Ref)
		{
			case KList list:
				list[NormalizeIndex(index, list.Count)] = value;
				break;
			case KArray array:
				array[NormalizeIndex(index, array.Length)] = value;
				break;
			default:
				throw new KernPanic(
					$"ListSet: ожидался list или array, получено {receiver.TypeName}");
		}
	}

	/// <summary>
	/// Стек <c>[str, индекс i32]</c>; выход — <c>char</c>. Отрицательный индекс считается
	/// с конца, выход за границы — паника (spec round 27).
	/// </summary>
	private static void StrGet(ref OperandStack stack)
	{
		KValue indexValue = stack.Pop();
		KValue receiver = stack.Pop();
		if (indexValue.Kind != ValueKind.I32)
			throw new KernPanic($"StrGet: ожидался индекс i32, получено {indexValue.TypeName}");
		if (receiver.Kind != ValueKind.Str)
			throw new KernPanic($"StrGet: ожидалась строка, получено {receiver.TypeName}");
		string text = receiver.AsStr;
		int index = NormalizeIndex(indexValue.AsI32, text.Length);
		if ((uint)index >= (uint)text.Length)
			throw new KernPanic($"StrGet: индекс {indexValue.AsI32} вне длины {text.Length}");
		stack.Push(KValue.Char(text[index]));
	}
}
