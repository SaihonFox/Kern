using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.CodeGen;

internal sealed partial class FunctionCompiler
{
	private void EmitUnary(BoundUnary u)
	{
		Emit(u.Operand);
		if (u.Operator is { } fn)
		{
			EmitInvoke(fn, 0, OwnerArgs(u.Operand.Type), [], hasReceiver: true, nonVirtual: false);
			return;
		}
		switch (u.Op)
		{
			case Syn.UnaryOp.Negate: B.Emit(OpCode.Neg); break;
			case Syn.UnaryOp.Not: B.Emit(OpCode.Not); break;
			case Syn.UnaryOp.Plus: break;
		}
	}

	private void EmitBinary(BoundBinary b)
	{
		switch (b.Op)
		{
			case Syn.BinaryOp.And:
			{
				var end = new Label();
				Emit(b.Left); B.Emit(OpCode.Dup); B.Jump(OpCode.JmpIfNot, end);
				B.Emit(OpCode.Pop); Emit(b.Right);
				B.Mark(end);
				return;
			}
			case Syn.BinaryOp.Or:
			{
				var end = new Label();
				Emit(b.Left); B.Emit(OpCode.Dup); B.Jump(OpCode.JmpIf, end);
				B.Emit(OpCode.Pop); Emit(b.Right);
				B.Mark(end);
				return;
			}
			case Syn.BinaryOp.In or Syn.BinaryOp.NotIn when b.Operator is { } inFn:
				Emit(b.Right); Emit(b.Left);
				EmitInvoke(inFn, 1, OwnerArgs(b.Right.Type), [], hasReceiver: true, nonVirtual: false);
				if (b.Op == Syn.BinaryOp.NotIn) B.Emit(OpCode.Not);
				return;
		}
		Emit(b.Left);
		Emit(b.Right);
		EmitBinaryOperation(b.Op, b.Operator, b.Left.Type);
	}

	/// <summary>Операнды [left, right] уже на стеке.</summary>
	private void EmitBinaryOperation(Syn.BinaryOp op, FunctionSymbol? fn, TypeSymbol leftType)
	{
		if (fn is not null)
		{
			EmitInvoke(fn, 1, OwnerArgs(leftType), [], hasReceiver: true, nonVirtual: false);
			if (op is Syn.BinaryOp.Ne && fn.OpSymbol == "==") B.Emit(OpCode.Not);
			// `a > b` через трёхсторонний `<=>`: результат (i32) сравнивается с нулём
			if (fn.OpSymbol == "<=>" && op is Syn.BinaryOp.Lt or Syn.BinaryOp.Gt or Syn.BinaryOp.Le or Syn.BinaryOp.Ge)
			{
				B.Emit(OpCode.LdI32, 0);
				B.Emit(op switch { Syn.BinaryOp.Lt => OpCode.Lt, Syn.BinaryOp.Gt => OpCode.Gt, Syn.BinaryOp.Le => OpCode.Le, _ => OpCode.Ge });
			}
			return;
		}
		B.Emit(op switch
		{
			Syn.BinaryOp.Add => OpCode.Add, Syn.BinaryOp.Sub => OpCode.Sub, Syn.BinaryOp.Mul => OpCode.Mul, Syn.BinaryOp.Div => OpCode.Div,
			Syn.BinaryOp.Rem => OpCode.Mod, Syn.BinaryOp.Pow => OpCode.Pow,
			Syn.BinaryOp.Eq => OpCode.Eq, Syn.BinaryOp.Ne => OpCode.Ne, Syn.BinaryOp.Lt => OpCode.Lt, Syn.BinaryOp.Gt => OpCode.Gt,
			Syn.BinaryOp.Le => OpCode.Le, Syn.BinaryOp.Ge => OpCode.Ge,
			Syn.BinaryOp.RefEq => OpCode.RefEq, Syn.BinaryOp.RefNe => OpCode.RefNe,
			Syn.BinaryOp.In => OpCode.In, Syn.BinaryOp.NotIn => OpCode.NotIn,
			_ => throw new NotSupportedException($"бинарная операция {op} не поддерживается"),
		});
	}

	private void EmitCoalesce(BoundCoalesce c)
	{
		var end = new Label();
		Emit(c.Left);
		B.Jump(OpCode.JmpIfNotNilOrNone, end); // не nil/none: значение остаётся, иначе снимается
		Emit(c.Right);
		B.Mark(end);
	}

	private static Syn.BinaryOp ToBinary(Syn.AssignOp op) => op switch
	{
		Syn.AssignOp.Add => Syn.BinaryOp.Add, Syn.AssignOp.Sub => Syn.BinaryOp.Sub, Syn.AssignOp.Mul => Syn.BinaryOp.Mul,
		Syn.AssignOp.Div => Syn.BinaryOp.Div, Syn.AssignOp.Rem => Syn.BinaryOp.Rem, Syn.AssignOp.Pow => Syn.BinaryOp.Pow,
		_ => throw new NotSupportedException($"составная операция {op}"),
	};
}
