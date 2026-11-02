using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>
/// Запись по встроенному индексу (spec §5): вынесена из основного цикла, чтобы
/// <c>Interpreter.RunLoop.cs</c> оставался в пределах 800 строк.
/// </summary>
public sealed partial class Interpreter
{
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
}
