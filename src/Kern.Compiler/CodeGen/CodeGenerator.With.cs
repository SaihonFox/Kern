using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;

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
			if (assignment.Field is { } field)
			{
				Emit(OpCode.StoreField, ClassFieldSlot(field));
				Emit(OpCode.Pop);
				continue;
			}

			FunctionSymbol setter = assignment.Property!.Setter!;
			bool isVirtual = setter.IsOpen || setter.IsOverride || setter.IsAbstract;
			EmitCallTo(setter, argumentCount: 2, assignment.Value.Span, isVirtual);
			Emit(OpCode.Pop);
		}

		Emit(OpCode.LoadLocal, slot);
	}
}
