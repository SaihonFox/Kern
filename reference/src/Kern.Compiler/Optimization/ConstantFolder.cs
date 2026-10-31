using System.Numerics;
using Kern.Bytecode;
using Kern.Compiler.Lexing;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Types;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Optimization;

/// <summary>Свёртка констант (-O1+): арифметика и сравнения над литералами, логика над bool, `if` с константным условием.</summary>
public static class ConstantFolder
{
	public static BoundExpr Fold(BoundExpr e) => e switch
	{
		BoundUnary u => FoldUnary(u),
		BoundBinary b => FoldBinary(b),
		BoundIf i => FoldIf(i),
		_ => e,
	};

	private static BoundExpr FoldIf(BoundIf i)
	{
		var c = Fold(i.Condition);
		if (c is BoundBoolLiteral { Value: true }) return Fold(i.Then);
		if (c is BoundBoolLiteral { Value: false } && i.Else is not null) return Fold(i.Else);
		return i;
	}

	private static BoundExpr FoldUnary(BoundUnary u)
	{
		if (u.Operator is not null) return u;
		var o = Fold(u.Operand);
		switch (u.Op)
		{
			case Syn.UnaryOp.Not when o is BoundBoolLiteral bl: return new BoundBoolLiteral(!bl.Value, u.Span);
			case Syn.UnaryOp.Plus when o is BoundNumberLiteral: return o;
			case Syn.UnaryOp.Negate when o is BoundNumberLiteral n && n.Type is PrimitiveType p && IsSigned(p.Kind):
			{
				var v = n.Value.IsFloat ? new NumberValue(0, -n.Value.Float, true, n.Value.Suffix) : new NumberValue(-n.Value.Integer, 0, false, n.Value.Suffix);
				if (!v.IsFloat && !InRange(p.Kind, v.Integer)) return u;
				return n with { Value = v, Span = u.Span };
			}
		}
		return u;
	}

	private static BoundExpr FoldBinary(BoundBinary b)
	{
		if (b.Operator is not null) return b;
		var l = Fold(b.Left); var r = Fold(b.Right);
		if (l is BoundBoolLiteral lb && r is BoundBoolLiteral rb)
		{
			bool? res = b.Op switch
			{
				Syn.BinaryOp.And => lb.Value && rb.Value, Syn.BinaryOp.Or => lb.Value || rb.Value,
				Syn.BinaryOp.Eq => lb.Value == rb.Value, Syn.BinaryOp.Ne => lb.Value != rb.Value, _ => null,
			};
			if (res is { } v) return new BoundBoolLiteral(v, b.Span);
		}
		if (l is BoundNumberLiteral ln && r is BoundNumberLiteral rn && ln.Type.Equals(rn.Type) && ln.Type is PrimitiveType pt)
		{
			if (!ln.Value.IsFloat && !rn.Value.IsFloat) return FoldInt(b, ln, rn, pt);
			if (pt.Kind is PrimKind.F64 or PrimKind.F32) return FoldFloat(b, ln, rn, pt);
		}
		return b;
	}

	private static BoundExpr FoldInt(BoundBinary b, BoundNumberLiteral l, BoundNumberLiteral r, PrimitiveType pt)
	{
		BigInteger x = l.Value.Integer, y = r.Value.Integer;
		BigInteger? v = b.Op switch
		{
			Syn.BinaryOp.Add => x + y, Syn.BinaryOp.Sub => x - y, Syn.BinaryOp.Mul => x * y,
			Syn.BinaryOp.Div when !y.IsZero => BigInteger.Divide(x, y),
			Syn.BinaryOp.Rem when !y.IsZero => BigInteger.Remainder(x, y),
			_ => null,
		};
		if (v is { } n)
			return InRange(pt.Kind, n) ? l with { Value = new NumberValue(n, 0, false, l.Value.Suffix), Span = b.Span } : b;
		bool? c = b.Op switch
		{
			Syn.BinaryOp.Eq => x == y, Syn.BinaryOp.Ne => x != y, Syn.BinaryOp.Lt => x < y,
			Syn.BinaryOp.Gt => x > y, Syn.BinaryOp.Le => x <= y, Syn.BinaryOp.Ge => x >= y, _ => null,
		};
		return c is { } cv ? new BoundBoolLiteral(cv, b.Span) : b;
	}

	private static BoundExpr FoldFloat(BoundBinary b, BoundNumberLiteral l, BoundNumberLiteral r, PrimitiveType pt)
	{
		double x = l.Value.Float, y = r.Value.Float;
		double? v = b.Op switch
		{
			Syn.BinaryOp.Add => x + y, Syn.BinaryOp.Sub => x - y, Syn.BinaryOp.Mul => x * y, Syn.BinaryOp.Div when y != 0 => x / y, _ => null,
		};
		if (v is { } d && double.IsFinite(d)) return l with { Value = new NumberValue(0, d, true, l.Value.Suffix), Span = b.Span };
		bool? c = b.Op switch
		{
			Syn.BinaryOp.Eq => x == y, Syn.BinaryOp.Ne => x != y, Syn.BinaryOp.Lt => x < y,
			Syn.BinaryOp.Gt => x > y, Syn.BinaryOp.Le => x <= y, Syn.BinaryOp.Ge => x >= y, _ => null,
		};
		return c is { } cv ? new BoundBoolLiteral(cv, b.Span) : b;
	}

	private static bool IsSigned(PrimKind k) => k is PrimKind.I8 or PrimKind.I16 or PrimKind.I32 or PrimKind.I64 or PrimKind.F16 or PrimKind.F32 or PrimKind.F64;

	private static bool InRange(PrimKind k, BigInteger v) => k switch
	{
		PrimKind.I8 => v >= sbyte.MinValue && v <= sbyte.MaxValue,
		PrimKind.I16 => v >= short.MinValue && v <= short.MaxValue,
		PrimKind.I32 => v >= int.MinValue && v <= int.MaxValue,
		PrimKind.I64 => v >= long.MinValue && v <= long.MaxValue,
		PrimKind.U8 => v >= 0 && v <= byte.MaxValue,
		PrimKind.U16 => v >= 0 && v <= ushort.MaxValue,
		PrimKind.U32 => v >= 0 && v <= uint.MaxValue,
		PrimKind.U64 => v >= 0 && v <= ulong.MaxValue,
		_ => false,
	};
}
