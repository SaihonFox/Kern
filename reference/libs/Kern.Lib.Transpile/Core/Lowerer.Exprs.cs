using Kern.Compiler.Semantics.Bound;
using Kern.Binding.Catalog;
using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Lib.Transpile.Core;

public abstract partial class Lowerer
{
	protected string Value(BoundExpr e)
	{
		switch (e)
		{
			case BoundNumberLiteral n: return NumberLit(n.Value, n.Type);
			case BoundStringLiteral s: return $"\"{Naming.Escape(s.Value, Id == "kotlin")}\"";
			case BoundCharLiteral c: return CharLit(c.CodePoint);
			case BoundBoolLiteral b: return b.Value ? "true" : "false";
			case BoundNilLiteral: return NilLit;
			case BoundNoneLiteral: return NoneLit;
			case BoundInterpolatedString i: return InterpolatedValue(i);
			case BoundLocal l: return NameOf(l.Local);
			case BoundParameter p: return NameOf(p.Parameter);
			case BoundThis: return ThisAlias ?? "this";
			case BoundParentThis pt: return BaseAccess(pt.Parent);
			case BoundField f: return FieldAccess(f);
			case BoundProperty p: return PropertyAccess(p);
			case BoundIndex ix: return IndexAccess(ix);
			case BoundTupleItem ti: return TupleItemText(Value(ti.Target), ti.Index, (ti.Target.Type as TupleType)?.Items.Count ?? 2);
			case BoundFunctionRef fr: return FunctionRef(fr);
			case BoundNonNull nn: return NonNullText(Value(nn.Operand));
			case BoundCall c: return CallValue(c);
			case BoundDelegateCall dc:
			{
				var callee = Value(dc.Callee);
				var args = dc.Arguments.Select(Value).ToList();
				return CallText(callee, "", [], args, dc.IsSafe);
			}
			case BoundNew nw: return NewText(nw.Type, nw.Arguments.Select(Value).ToList());
			case BoundDefaultArgument da: return da.Value is null ? NullDefault(da.Type) : Value(da.Value);
			case BoundConversion cv: return ConversionValue(cv);
			case BoundList li: return ListLit(li.ElementType, li.Elements.Select(Value).ToList());
			case BoundArray ar: return ArrayLit(ar.ElementType, ar.Elements.Select(Value).ToList());
			case BoundDict di: return DictLit((DictType)di.Type, di.Entries.Select(x => (Value(x.Key), Value(x.Value))).ToList());
			case BoundTuple tu: return TupleLit(tu.Elements.Select(Value).ToList());
			case BoundRange r:
				return RangeLit(r.Start is null ? null : Value(r.Start), r.End is null ? null : Value(r.End), r.Inclusive, r.Step is null ? null : Value(r.Step));
			case BoundAwait aw: return AwaitText(Value(aw.Operand));
			case BoundLambda la: return LambdaValue(la);
			case BoundUnary u: return UnaryValue(u);
			case BoundBinary bi: return BinaryValue(bi);
			case BoundCoalesce co: return CoalesceValue(co);
			case BoundTypeTest tt: return IsType(Value(tt.Operand), tt.TestType, tt.Negated);
			case BoundResultCreate rc: return ResultText(rc.IsOk, rc.Value is null ? null : Value(rc.Value), rc.Type);
			case BoundTypeOf to: return TypeOfText(to.Target);
			case BoundAssignment or BoundCompoundAssignment:
			{
				Stmt(e, Dest.Discard);
				return Value(e is BoundAssignment a ? a.Target : ((BoundCompoundAssignment)e).Target);
			}
			case BoundIf i when TryInlineIf(i) is { } inline: return inline;
			case BoundBlock { Result: { } res } blk when blk.Statements.Count == 0 && blk.Locals.Count == 0: return Value(res);
			case BoundErrorExpr: return "/* error */";
			default: return Hoist(e);
		}
	}

	/// <summary>Выражение управления потоком в позиции значения: во временную переменную.</summary>
	private string Hoist(BoundExpr e)
	{
		if (e.Type is NeverType || IsVoid(e.Type)) { Stmt(e, Dest.Discard); return NoneLit; }
		var t = Temp();
		DeclareLocal(t, e.Type, null, false);
		Stmt(e, Dest.Assign(t));
		return t;
	}

	private string CharLit(int cp)
	{
		string s = char.ConvertFromUtf32(cp);
		string body = s switch { "'" => "\\'", "\\" => "\\\\", "\n" => "\\n", "\r" => "\\r", "\t" => "\\t", "\0" => "\\u0000", _ => s };
		return $"'{body}'";
	}

	private string InterpolatedValue(BoundInterpolatedString i)
	{
		var parts = new List<(string?, string?)>();
		foreach (var p in i.Parts)
			switch (p)
			{
				case BoundInterpolationText t: parts.Add((t.Text, null)); break;
				case BoundInterpolationValue v: parts.Add((null, StripParens(Value(v.Value)))); break;
			}
		return Interpolated(parts);
	}

	private string ConversionValue(BoundConversion cv)
	{
		if (cv.Operand is BoundNumberLiteral n && cv.Type is PrimitiveType) return NumberLit(n.Value, cv.Type);
		var v = Value(cv.Operand);
		return cv.Kind == ConversionKind.ImplicitNumeric ? ConvertText(v, cv.Operand.Type, cv.Type) : v;
	}

	// ------------------------------------------------------------------ доступ к членам

	private string Qualifier(BoundExpr? receiver, Symbol member, bool isStatic, out bool global)
	{
		global = false;
		if (receiver is not null) return Value(receiver);
		if (member.Container is ModuleSymbol) { global = true; return ""; }
		if (isStatic && member.DeclaringType is { } dt) return TypeName(dt.ThisType) is var tn ? StripGenerics(tn) : "";
		return ThisAlias ?? "this";
	}

	private static string StripGenerics(string t)
	{
		int i = t.IndexOf('<');
		return i < 0 ? t : t[..i];
	}

	private string FieldAccess(BoundField f)
	{
		if (f.Field.BackingFor is not null) return "field";
		string name = f.Field.IsEnumEntry ? Naming.Pascal(f.Field.Name) : MemberName(f.Field.Name);
		if (f.Field.IsGlobal && f.Receiver is null) return GlobalRef(f.Field, name);
		var q = Qualifier(f.Receiver, f.Field, f.Field.IsStatic, out _);
		if (f.Field.IsEnumEntry && f.Receiver is null) return $"{StripGenerics(TypeName(f.Field.DeclaringType!.ThisType))}.{name}";
		return $"{q}{(f.IsSafe ? "?." : ".")}{name}";
	}

	private string PropertyAccess(BoundProperty p)
	{
		var prop = p.Property;
		if (prop.Native is { } nat)
		{
			string owner = nat.Owner?.Name ?? "";
			string recv = p.Receiver is null ? "" : Value(p.Receiver);
			var mapped = Maps.MapMember(Id, owner, prop.Name, recv);
			if (mapped is not null) return mapped;
			Unmapped.Add($"{owner}.{prop.Name}");
			return $"{recv}{(p.IsSafe ? "?." : ".")}{MemberName(prop.Name)}";
		}
		if (prop.ExtensionOf is not null && p.Receiver is not null)
			return $"{Value(p.Receiver)}{(p.IsSafe ? "?." : ".")}{MemberName(prop.Name)}";
		var q = Qualifier(p.Receiver, prop, prop.IsStatic, out bool global);
		if (global) return GlobalRef(prop, MemberName(prop.Name));
		return $"{q}{(p.IsSafe ? "?." : ".")}{MemberName(prop.Name)}";
	}

	private string IndexAccess(BoundIndex ix)
	{
		var t = Value(ix.Target);
		var idx = string.Join(", ", ix.Indices.Select(Value));
		return ix.IsSafe ? SafeIndexText(t, idx) : $"{t}[{idx}]";
	}

	private string FunctionRef(BoundFunctionRef fr)
	{
		var f = fr.Function;
		string name = MemberName(f.Name);
		if (f.Container is ModuleSymbol) return FunctionRefText(null, GlobalRef(f, name), true);
		return FunctionRefText(fr.Receiver is null ? null : Value(fr.Receiver), name, false);
	}

	// ------------------------------------------------------------------ вызовы

	private string CallValue(BoundCall c)
	{
		var f = c.Function;
		string? recv = c.Receiver is null ? null : Value(c.Receiver);
		var args = c.Arguments.Select(Value).ToList();

		if (f is NativeMemberSymbol nm)
		{
			string owner = nm.Member.Owner?.Name ?? "";
			var mapped = Maps.MapCall(Id, owner, f.Name, recv ?? "", args);
			if (mapped is not null) return mapped;
			Unmapped.Add($"{owner}.{f.Name}");
			string fallback = owner.Length == 0 || nm.Member.IsStatic ? (owner.Length == 0 ? "" : Naming.Pascal(owner) + ".") : "";
			return recv is null
				? CallText(null, fallback + MemberName(f.Name), c.TypeArgs, args, false)
				: CallText(recv, MemberName(f.Name), c.TypeArgs, args, c.IsSafe);
		}

		string name = MemberName(f.Name);
		if (f.IsOperator && f.OpSymbol is not null) name = "op";
		if (recv is not null) return CallText(recv, name, c.TypeArgs, args, c.IsSafe);
		if (f.Container is ModuleSymbol) return CallText(null, GlobalRef(f, name), c.TypeArgs, args, false);
		if (f.IsStatic && f.DeclaringType is { } dt) return CallText(StripGenerics(TypeName(dt.ThisType)), name, c.TypeArgs, args, false);
		return CallText(ThisAlias ?? "this", name, c.TypeArgs, args, false);
	}

	// ------------------------------------------------------------------ операторы

	private string UnaryValue(BoundUnary u)
	{
		var v = Value(u.Operand);
		return u.Op switch
		{
			Syn.UnaryOp.Negate => $"(-{v})",
			Syn.UnaryOp.Plus => v,
			_ => $"(!{v})",
		};
	}

	private string BinaryValue(BoundBinary b)
	{
		var l = Value(b.Left);
		// короткое замыкание: правая часть с побочными операторами требует отдельной ветки
		var (r, rt) = Capture(() => Value(b.Right));
		if (rt.Length != 0 && b.Op is Syn.BinaryOp.And or Syn.BinaryOp.Or)
		{
			var t = Temp();
			DeclareLocal(t, PrimitiveType.Bool, l, false);
			W.Open(b.Op == Syn.BinaryOp.And ? $"if ({t})" : $"if (!{t})");
			W.Line(rt.TrimEnd('\n'));
			W.Line($"{t} = {StripParens(r)}{End}");
			W.Close();
			return t;
		}
		if (rt.Length != 0) W.Line(rt.TrimEnd('\n'));
		return b.Op switch
		{
			Syn.BinaryOp.Add => $"({l} + {r})",
			Syn.BinaryOp.Sub => $"({l} - {r})",
			Syn.BinaryOp.Mul => $"({l} * {r})",
			Syn.BinaryOp.Div => $"({l} / {r})",
			Syn.BinaryOp.Rem => $"({l} % {r})",
			Syn.BinaryOp.Pow => Pow(l, r),
			Syn.BinaryOp.Eq => $"({l} == {r})",
			Syn.BinaryOp.Ne => $"({l} != {r})",
			Syn.BinaryOp.RefEq => RefEq(l, r, false),
			Syn.BinaryOp.RefNe => RefEq(l, r, true),
			Syn.BinaryOp.Lt => $"({l} < {r})",
			Syn.BinaryOp.Gt => $"({l} > {r})",
			Syn.BinaryOp.Le => $"({l} <= {r})",
			Syn.BinaryOp.Ge => $"({l} >= {r})",
			Syn.BinaryOp.And => $"({l} && {r})",
			Syn.BinaryOp.Or => $"({l} || {r})",
			Syn.BinaryOp.In => InText(l, r, false),
			Syn.BinaryOp.NotIn => InText(l, r, true),
			Syn.BinaryOp.Elvis => CoalesceText(l, r),
			_ => $"({l} ?? {r})",
		};
	}

	private string CoalesceValue(BoundCoalesce co)
	{
		var l = Value(co.Left);
		if (co.Right.Type is NeverType)
		{
			string t = IsSimple(l) ? l : HoistTemp(l);
			W.Open($"if ({t} == null)");
			Stmt(co.Right, Dest.Discard);
			W.Close();
			return NonNullText(t);
		}
		var (r, rt) = Capture(() => Value(co.Right));
		if (rt.Length == 0) return CoalesceText(l, r);
		string tmp = IsSimple(l) ? l : HoistTemp(l);
		var res = Temp();
		DeclareLocal(res, co.Type, tmp, false);
		W.Open($"if ({tmp} == null)");
		W.Line(rt.TrimEnd('\n'));
		W.Line($"{res} = {StripParens(r)}{End}");
		W.Close();
		return res;
	}

	// ------------------------------------------------------------------ лямбды

	private string LambdaValue(BoundLambda la)
	{
		var ps = la.Parameters.Select(p => (NameOf(p), TypeName(p.Type))).ToList();
		var body = la.Body;
		bool voidRet = IsVoid(la.ReturnType);
		var (v, text) = Capture(() => Value(body));
		if (text.Length == 0 && body is not BoundBlock && body is not BoundReturn)
			return Lambda(ps, v, null, la.ReturnType, la.IsAsync);
		var saved = W;
		W = new CodeWriter { Level = 1 };
		try
		{
			Stmt(body, voidRet ? Dest.Discard : Dest.Return);
			return Lambda(ps, null, W.ToString().TrimEnd('\n'), la.ReturnType, la.IsAsync);
		}
		finally { W = saved; }
	}
}
