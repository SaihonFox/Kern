using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;

namespace Kern.Compiler.CodeGen;

/// <summary>
/// Генерация Elvis <c>left ?: right</c> и <c>ret</c> в его правой ветви (spec §3/§4).
/// Новых опкодов нет: ветвление строится на <c>Dup</c>/<c>PushNil</c>/<c>PushNone</c>/
/// <c>Eq</c>/<c>JmpIf</c> и переходах.
/// </summary>
public sealed partial class CodeGenerator
{
	/// <summary>
	/// Левая часть вычисляется один раз; <c>nil</c> и <c>none</c> означают «значения нет» и
	/// заменяются правой частью. Значение слева остаётся на стеке для обеих ветвей, правая
	/// ветвь снимает заглушку перед вычислением.
	/// </summary>
	private void EmitElvis(BoundElvisExpression elvis)
	{
		EmitExpression(elvis.Left);
		Emit(OpCode.Dup);
		Emit(OpCode.PushNil);
		Emit(OpCode.Eq);
		var nilBranch = EmitJump(OpCode.JmpIf);
		Emit(OpCode.Dup);
		Emit(OpCode.PushNone);
		Emit(OpCode.Eq);
		var noneBranch = EmitJump(OpCode.JmpIf);
		var end = EmitJump(OpCode.Jmp);

		Patch(nilBranch, Position);
		Patch(noneBranch, Position);
		Emit(OpCode.Pop);
		EmitExpression(elvis.Right);
		Patch(end, Position);
	}

	/// <summary><c>ret value</c> в правой ветви Elvis: значение, очистка use-кадров, возврат.</summary>
	private void EmitReturnExpression(BoundReturnExpression ret)
	{
		if (ret.Value is not null && CanTail(ret.Value))
		{
			EmitTail(ret.Value);
			return;
		}

		if (ret.Value is null)
			Emit(OpCode.PushVoid);
		else
		{
			EmitExpression(ret.Value);
			EmitCopyIfUnit(ret.Value.Type);
		}
		EmitDisposalsThrough(0);
		Emit(OpCode.Ret);
	}
}
