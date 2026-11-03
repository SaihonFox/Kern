using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;

namespace Kern.Compiler.CodeGen;

/// <summary>
/// Аргументы <c>&amp;x</c>/<c>*x</c> (spec §6): <c>&amp;</c> передаёт значение, <c>*</c> —
/// ячейку на место вызова (<c>RefLocal</c>/<c>RefField</c>/<c>RefStatic</c>).
/// </summary>
public sealed partial class CodeGenerator
{
	private void EmitReferenceArgument(BoundReferenceArgumentExpression reference)
	{
		if (!reference.Mutable)
		{
			EmitExpression(reference.Operand);
			return;
		}

		switch (reference.Operand)
		{
			case BoundVariableExpression variable:
				// Сам ref-параметр уже держит ячейку — передаём её дальше без обёртки.
				if (_refSlots.Contains(variable.Slot))
					Emit(OpCode.LoadLocal, variable.Slot);
				else if (_boxedSlots.Contains(variable.Slot))
					Emit(OpCode.LoadLocal, variable.Slot); // KCell сам реализует IValueCell
				else
					Emit(OpCode.RefLocal, variable.Slot);
				return;

			case BoundFieldExpression { Field.IsStatic: true } staticField:
				if (_staticSlots.TryGetValue(staticField.Field, out var staticSlot))
					Emit(OpCode.RefStatic, staticSlot);
				else
				{
					Report("KRN2201", $"Статическое поле '{staticField.Field.Name}' не попало в FieldPool (M3-D)", reference.Span);
					Emit(OpCode.PushNone);
				}
				return;

			case BoundFieldExpression field:
				if (field.Receiver is not null)
					EmitReferenceReceiver(field.Receiver);
				else
					Emit(OpCode.LoadLocal, 0);
				if (_fieldSlots.TryGetValue(field.Field, out var fieldSlot))
					Emit(OpCode.RefField, fieldSlot);
				else
				{
					Report("KRN2201", $"Поле '{field.Field.Name}' не попало в TypeDef (M3-D)", reference.Span);
					Emit(OpCode.PushNone);
				}
				return;

			case BoundCaptureExpression { IsCell: true } capture:
				Emit(OpCode.LoadCapture, capture.Index);
				return;

			default:
				Report("KRN2201", "Маркер '*' применим только к переменной или полю", reference.Span);
				Emit(OpCode.PushNone);
				return;
		}
	}

	/// <summary>
	/// Получатель для <c>RefField</c>: нужен настоящий объект, поэтому unit-поля читаются
	/// без обычного копирования (<see cref="EmitExpression"/> копировал бы unit).
	/// </summary>
	private void EmitReferenceReceiver(BoundExpression receiver)
	{
		switch (receiver)
		{
			case BoundVariableExpression variable:
				if (_refSlots.Contains(variable.Slot))
				{
					Emit(OpCode.LoadLocal, variable.Slot);
					Emit(OpCode.LoadRef);
				}
				else
				{
					Emit(OpCode.LoadLocal, variable.Slot);
					if (_boxedSlots.Contains(variable.Slot))
						Emit(OpCode.LoadCell);
				}
				return;
			case BoundFieldExpression { Field.IsStatic: false } field:
				if (field.Receiver is not null)
					EmitReferenceReceiver(field.Receiver);
				else
					Emit(OpCode.LoadLocal, 0);
				if (_fieldSlots.TryGetValue(field.Field, out var slot))
					Emit(OpCode.LoadField, slot);
				else
				{
					Report("KRN2201", $"Поле '{field.Field.Name}' не попало в TypeDef (M3-D)", field.Span);
					Emit(OpCode.PushVoid);
				}
				return;
			default:
				EmitExpression(receiver);
				return;
		}
	}
}
