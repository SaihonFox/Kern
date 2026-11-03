using Kern.Bytecode;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>Опкоды in/ref-параметров (spec §6): ячейки на локальный слот, поле и статику.</summary>
public sealed partial class Interpreter
{
	private void RefOp(OpCode op, ref OperandStack stack, ref int pc, int[] code, KValue[] locals)
	{
		switch (op)
		{
			case OpCode.LoadRef:
				{
					KValue cellValue = stack.Pop();
					if (cellValue.Ref is not IValueCell cell)
						throw new KernPanic($"LoadRef: ожидалась ячейка значения, получено {cellValue.TypeName}");
					stack.Push(cell.Get());
					break;
				}
			case OpCode.StoreRef:
				{
					KValue value = stack.Pop();
					KValue cellValue = stack.Pop();
					if (cellValue.Ref is not IValueCell cell)
						throw new KernPanic($"StoreRef: ожидалась ячейка значения, получено {cellValue.TypeName}");
					cell.Set(value);
					break;
				}
			case OpCode.RefLocal:
				stack.Push(KValue.Obj(new LocalValueCell(locals, code[pc++])));
				break;
			case OpCode.RefField:
				{
					KValue receiverValue = stack.Pop();
					if (receiverValue.Ref is not KObject target)
						throw new KernPanic($"RefField: ожидался объект, получено {receiverValue.TypeName}");
					stack.Push(KValue.Obj(new FieldValueCell(target, code[pc++])));
					break;
				}
			case OpCode.RefStatic:
				stack.Push(KValue.Obj(new StaticValueCell(_statics, code[pc++])));
				break;
		}
	}
}
