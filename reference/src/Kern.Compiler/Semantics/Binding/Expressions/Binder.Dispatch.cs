using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Types;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding.Core;

public sealed partial class Binder
{
	/// <summary>Привязывает выражение-значение. <paramref name="expected"/> — ожидаемый тип (подсказка для лямбд, коллекций, Ok/Err).</summary>
	public BoundExpr Bind(Syn.Expr e, TypeSymbol? expected = null)
	{
		switch (e)
		{
			case Syn.ErrorExpr:
				return new BoundErrorExpr(e.Span);

			case Syn.NumberLitExpr n:
				return BindNumber(n);
			case Syn.StringLitExpr s:
				return new BoundStringLiteral(s.Value, s.Span);
			case Syn.CharLitExpr c:
				return new BoundCharLiteral(c.CodePoint, c.Span);
			case Syn.BoolLitExpr b:
				return new BoundBoolLiteral(b.Value, b.Span);
			case Syn.NilLitExpr:
				return new BoundNilLiteral(e.Span);
			case Syn.NoneLitExpr:
				return new BoundNoneLiteral(e.Span);
			case Syn.InterpolatedStringExpr i:
				return BindInterpolated(i);

			case Syn.IdentExpr or Syn.MemberExpr or Syn.SelfExpr or Syn.SelfMemberExpr or Syn.ParentMemberExpr:
				return ToValue(BindTarget(e, []), expected);

			case Syn.UnaryExpr u:
				return BindUnary(u, expected);
			case Syn.BinaryExpr b:
				return BindBinary(b, expected);
			case Syn.TypeTestExpr t:
				return BindTypeTest(t);
			case Syn.AssignExpr a:
				return BindAssign(a);
			case Syn.NonNullExpr nn:
				return BindNonNull(nn);

			case Syn.CallExpr c:
				return BindCall(c, expected);
			case Syn.IndexExpr ix:
				return BindIndex(ix);
			case Syn.TypeofTypeExpr tt:
				return new BoundTypeOf(ResolveType(tt.Type), tt.Span);
			case Syn.TypeofValueExpr tv:
				return BindTypeofValue(tv);
			case Syn.AwaitExpr aw:
				return BindAwait(aw);

			case Syn.IfExpr f:
				return BindIf(f, expected);
			case Syn.TernaryExpr t:
				return BindTernary(t, expected);
			case Syn.MatchExpr m:
				return BindMatch(m, expected);
			case Syn.LoopExpr l:
				return BindLoop(l);
			case Syn.WhileExpr w:
				return BindWhile(w);
			case Syn.StopExpr:
				return BindStop(e.Span);
			case Syn.NextExpr:
				return BindNext(e.Span);
			case Syn.ReturnExpr r:
				return BindReturn(r);
			case Syn.RangeExpr r:
				return BindRange(r, expected);
			case Syn.BlockExpr bl:
				return BindBlock(bl, expected);

			case Syn.LambdaExpr lam:
				return BindLambda(lam, expected);

			case Syn.VarDeclExpr or Syn.DestructureExpr:
				return BindDeclarationAsExpression(e);

			case Syn.TupleExpr tup:
				return BindTuple(tup, expected);
			case Syn.ListExpr li:
				return BindListLiteral(li, expected);
			case Syn.DictExpr d:
				return BindDictLiteral(d, expected);
			case Syn.ArrayExpr ar:
				return BindArrayLiteral(ar, expected);

			default:
				return ErrorExpr(Krn.NotAValue, $"неподдерживаемое выражение {e.GetType().Name}", e.Span);
		}
	}

	/// <summary>Привязывает выражение и приводит к целевому типу.</summary>
	public BoundExpr BindConverted(Syn.Expr e, TypeSymbol target)
	{
		var b = Bind(e, target);
		return Convert(b, target, e.Span);
	}

	// ------------------------------------------------------------ литералы

	private BoundExpr BindNumber(Syn.NumberLitExpr n)
	{
		var v = n.Value;
		TypeSymbol type = DefaultNumberType(v);
		if (v.Suffix is { } suffix)
		{
			if (PrimitiveType.FromName(suffix) is { IsNumeric: true } p) type = p;
			else return ErrorExpr(Krn.TypeMismatch, $"неизвестный суффикс числового литерала '{suffix}'", n.Span);
		}
		else if (!v.IsFloat && v.Integer > ulong.MaxValue)
			return ErrorExpr(Krn.NumberLiteralOutOfRange, "числовой литерал не помещается ни в один целый тип", n.Span);
		return new BoundNumberLiteral(v, type, n.Span);
	}

	private BoundExpr BindInterpolated(Syn.InterpolatedStringExpr s)
	{
		var parts = new List<BoundInterpolationPart>();
		foreach (var p in s.Parts)
		{
			switch (p)
			{
				case Syn.InterpText t:
					parts.Add(new BoundInterpolationText(t.Text, t.Span));
					break;
				case Syn.InterpExpr x:
				{
					var v = Bind(x.Value);
					if (!v.IsError && v.Type is PrimitiveType { Kind: Kern.Bytecode.PrimKind.Void })
						Error(Krn.TypeMismatch, "в интерполяции выражение не имеет значения (void)", x.Span);
					parts.Add(new BoundInterpolationValue(v, x.Debug, x.Format, x.SourceText, x.Span));
					break;
				}
			}
		}
		return new BoundInterpolatedString(parts, s.Span);
	}
}
