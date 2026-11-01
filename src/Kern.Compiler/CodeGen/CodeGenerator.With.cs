using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;

namespace Kern.Compiler.CodeGen;

public sealed partial class CodeGenerator
{
	/// <summary>
	/// <c>with</c> (spec round 27): получить значение, склонировать его (вложенные unit
	/// копируются, ссылки остаются общими) и записать заменяемые поля в копию. Копия живёт
	/// в локальном слоте, чтобы исходное значение не изменилось.
	/// </summary>
	private void EmitWith(BoundWithExpression expression)
	{
		EmitExpression(expression.Target);
		Emit(OpCode.CloneObject);
		int slot = AllocateLocalSlot();
		Emit(OpCode.StoreLocal, slot);
		foreach (BoundWithAssignment assignment in expression.Assignments)
		{
			Emit(OpCode.LoadLocal, slot);
			EmitExpression(assignment.Value);
			Emit(OpCode.StoreField, ClassFieldSlot(assignment.Field));
			Emit(OpCode.Pop);
		}

		Emit(OpCode.LoadLocal, slot);
	}
}
