using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Bound.Patterns;
using Kern.Compiler.Semantics.Bound.Statements;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Lib.Transpile.Core;

public abstract partial class Lowerer
{
	// ------------------------------------------------------------------ операторы

	protected void Stmt(BoundExpr e, Dest d)
	{
		if (d.Kind != DestKind.Discard && IsVoid(e.Type) && e is not BoundReturn and not BoundStop and not BoundNext) d = Dest.Discard;
		switch (e)
		{
			case BoundBlock b: Block(b, d); break;
			case BoundIf i: IfStmt(i, d); break;
			case BoundMatch m: MatchStmt(m, d); break;
			case BoundLoop l: LoopStmt(l, d); break;
			case BoundWhile w: WhileStmt(w, d); break;
			case BoundStop: W.Line("break" + End); break;
			case BoundNext: W.Line("continue" + End); break;
			case BoundReturn r:
				if (r.Value is null) W.Line("return" + End);
				else Stmt(r.Value, Dest.Return);
				break;
			case BoundAssignment a:
			{
				var target = Value(a.Target);
				var v = Value(a.Value);
				W.Line($"{target} = {StripParens(v)}{End}");
				Finish(d, target, a);
				break;
			}
			case BoundCompoundAssignment c:
			{
				var target = Value(c.Target);
				var v = Value(c.Value);
				W.Line(CompoundText(c, target, v) + End);
				Finish(d, target, c);
				break;
			}
			default:
			{
				var v = Value(e);
				Finish(d, v, e);
				break;
			}
		}
	}

	protected abstract string End { get; }

	private string CompoundText(BoundCompoundAssignment c, string target, string v) => c.Op switch
	{
		Syn.AssignOp.Add => $"{target} += {StripParens(v)}",
		Syn.AssignOp.Sub => $"{target} -= {StripParens(v)}",
		Syn.AssignOp.Mul => $"{target} *= {StripParens(v)}",
		Syn.AssignOp.Div => $"{target} /= {StripParens(v)}",
		Syn.AssignOp.Rem => $"{target} %= {StripParens(v)}",
		Syn.AssignOp.Pow => $"{target} = {Pow(target, v)}",
		_ => $"{target} = {StripParens(v)}",
	};

	/// <summary>Доставляет готовое значение по назначению.</summary>
	protected void Finish(Dest d, string value, BoundExpr source)
	{
		switch (d.Kind)
		{
			case DestKind.Discard:
				if (source is BoundAssignment or BoundCompoundAssignment) break; // уже выведено
				if (source is BoundLocal or BoundParameter or BoundNumberLiteral or BoundStringLiteral or BoundBoolLiteral) break;
				if (source.Type is NeverType) break;
				W.Line(StatementText(value));
				break;
			case DestKind.Return:
				W.Line($"return {StripParens(value)}{End}");
				break;
			case DestKind.Assign:
				if (source is BoundAssignment or BoundCompoundAssignment && value == d.Target) break;
				W.Line($"{d.Target} = {StripParens(value)}{End}");
				break;
			case DestKind.AddTo:
				W.Line(AddToText(d.Target!, StripParens(value)));
				break;
		}
	}

	private string StatementText(string v)
	{
		if (AlwaysStatement) return v;
		bool stmt = v.StartsWith("throw ") || v.StartsWith("await ") || (v.EndsWith(')') && !v.StartsWith('('));
		return stmt ? v + ";" : DiscardText(v);
	}

	// ------------------------------------------------------------------ блоки

	private void Block(BoundBlock b, Dest d)
	{
		int tries = 0;
		var disposers = new List<BoundExpr>();
		foreach (var s in b.Statements)
		{
			Statement(s);
			if (s is BoundLocalDeclaration { Dispose: { } disp })
			{
				W.Open("try");
				disposers.Add(disp);
				tries++;
			}
		}
		if (b.Result is { } r) Stmt(r, d);
		else if (d.Kind == DestKind.Return && !IsVoid(b.Type)) { /* значение вернул внутренний ret */ }
		for (int i = tries - 1; i >= 0; i--)
		{
			W.CloseOpen("finally");
			Stmt(disposers[i], Dest.Discard);
			W.Close();
		}
	}

	private void Statement(BoundStmt s)
	{
		switch (s)
		{
			case BoundExpressionStatement es: Stmt(es.Expression, Dest.Discard); break;
			case BoundLocalDeclaration ld: LocalDecl(ld); break;
			case BoundDestructuring ds:
			{
				var v = Value(ds.Value);
				string subject = IsSimple(v) ? v : HoistTemp(v);
				var binds = new List<Bind>();
				PatternCond(ds.Pattern, subject, binds);
				EmitBinds(binds);
				break;
			}
		}
	}

	private void LocalDecl(BoundLocalDeclaration ld)
	{
		string name = NameOf(ld.Local);
		var type = ld.Local.Type ?? ld.Initializer?.Type;
		if (ld.Initializer is null)
		{
			DeclareLocal(name, type, null, ld.Local.IsSingleAssign);
			return;
		}
		if (type is not null && ld.Initializer is BoundMatch or BoundLoop or BoundWhile or BoundBlock)
		{
			DeclareLocal(name, type, null, false);
			Stmt(ld.Initializer, Dest.Assign(name));
			return;
		}
		if (ld.Initializer is BoundIf i)
		{
			var inline = TryInlineIf(i);
			if (inline is not null) { DeclareLocal(name, type, inline, ld.Local.IsSingleAssign); return; }
			DeclareLocal(name, type, null, false);
			Stmt(i, Dest.Assign(name));
			return;
		}
		var init = Value(ld.Initializer);
		DeclareLocal(name, type, StripParens(init), ld.Local.IsSingleAssign);
	}

	private string HoistTemp(string value)
	{
		var t = Temp();
		DeclareLocal(t, null, StripParens(value), true);
		return t;
	}

	protected static bool IsSimple(string s) => s.All(c => char.IsLetterOrDigit(c) || c is '_' or '.');

	// ------------------------------------------------------------------ if

	private string? TryInlineIf(BoundIf i)
	{
		if (i.Else is null || IsVoid(i.Type)) return null;
		var (c, ct) = Capture(() => Value(i.Condition));
		var (a, at) = Capture(() => Value(i.Then));
		var (b, bt) = Capture(() => Value(i.Else));
		if (ct.Length + at.Length + bt.Length != 0) return null;
		if (i.Then.Type is NeverType || i.Else.Type is NeverType) return null;
		return Ternary(c, a, b);
	}

	private void IfStmt(BoundIf i, Dest d)
	{
		W.Open($"if ({StripParens(Value(i.Condition))})");
		Stmt(i.Then, d);
		var e = i.Else;
		while (e is BoundIf ei)
		{
			var (c, ct) = Capture(() => Value(ei.Condition));
			if (ct.Length != 0) break;
			W.CloseOpen($"else if ({StripParens(c)})");
			Stmt(ei.Then, d);
			e = ei.Else;
		}
		if (e is not null)
		{
			W.CloseOpen("else");
			Stmt(e, d);
		}
		W.Close();
	}

	// ------------------------------------------------------------------ match

	protected sealed record Bind(LocalSymbol? Local, string Expr, bool IsAssign);

	private void MatchStmt(BoundMatch m, Dest d)
	{
		string subject = "";
		if (m.Target is not null)
		{
			var v = Value(m.Target);
			subject = IsSimple(v) ? v : HoistTemp(v);
		}
		bool open = false, catchAll = false;
		foreach (var arm in m.Arms)
		{
			var binds = new List<Bind>();
			string? cond = m.Target is null ? null : PatternCond(arm.Pattern, subject, binds);
			string? guard = null;
			if (arm.Guard is { } g)
			{
				foreach (var b in binds.Where(b => b.Local is not null && !b.IsAssign)) _aliases[b.Local!] = "(" + b.Expr + ")";
				var (gv, gt) = Capture(() => Value(g));
				if (gt.Length != 0) Warnings.Add("сложное условие guard в match упрощено");
				guard = StripParens(gv);
			}
			string? full = (cond, guard) switch
			{
				(null, null) => null,
				({ } c, null) => c,
				(null, { } gg) => gg,
				({ } c, { } gg) => $"{c} && {gg}",
			};
			if (full is null)
			{
				if (open) W.CloseOpen("else"); else W.Open("");
				open = true;
				if (arm.Guard is null) EmitBinds(binds);
				Stmt(arm.Body, d);
				catchAll = true;
				break;
			}
			string head = $"if ({full})";
			if (open) W.CloseOpen("else " + head); else W.Open(head);
			open = true;
			if (arm.Guard is null) EmitBinds(binds);
			Stmt(arm.Body, d);
		}
		if (!open) return;
		if (!catchAll && d.Kind != DestKind.Discard)
		{
			W.CloseOpen("else");
			W.Line(ThrowText("no match"));
		}
		W.Close();
	}

	private void EmitBinds(IEnumerable<Bind> binds)
	{
		foreach (var b in binds)
		{
			if (b.Local is null) continue;
			if (b.IsAssign) W.Line($"{NameOf(b.Local)} = {b.Expr}{End}");
			else DeclareLocal(NameOf(b.Local), b.Local.Type, b.Expr, true);
		}
	}

	// ------------------------------------------------------------------ паттерны

	/// <summary>Условие совпадения (null — всегда истинно); попутно собирает привязки.</summary>
	protected string? PatternCond(BoundPattern p, string subj, List<Bind> binds)
	{
		switch (p)
		{
			case BoundWildcardPattern: return null;
			case BoundBindingPattern b:
				binds.Add(new Bind(b.Local, subj, b.IsAssignment));
				return null;
			case BoundValuePattern v:
				if (v.Value is BoundRange r) return RangeCond(r, subj);
				return $"{subj} == {Value(v.Value)}";
			case BoundTypePattern t:
				if (t.Local is not null) binds.Add(new Bind(t.Local, Cast(subj, t.TestType), false));
				return IsType(subj, t.TestType, false);
			case BoundDestructurePattern dp: return DestructureCond(dp, subj, binds);
			default: return null;
		}
	}

	private string RangeCond(BoundRange r, string subj)
	{
		var parts = new List<string>();
		if (r.Start is not null) parts.Add($"{subj} >= {Value(r.Start)}");
		if (r.End is not null) parts.Add($"{subj} {(r.Inclusive ? "<=" : "<")} {Value(r.End)}");
		return parts.Count == 0 ? "true" : "(" + string.Join(" && ", parts) + ")";
	}

	private string? DestructureCond(BoundDestructurePattern dp, string subj, List<Bind> binds)
	{
		var conds = new List<string>();
		void Add(string? c) { if (c is not null) conds.Add(c); }
		int n = dp.Elements.Count;
		switch (dp.Kind)
		{
			case DestructureKind.Tuple:
				for (int i = 0; i < n; i++) Add(PatternCond(dp.Elements[i], TupleItemText(subj, i, n), binds));
				break;
			case DestructureKind.Collection:
			{
				int rest = -1;
				for (int i = 0; i < n; i++) if (dp.Elements[i] is BoundRestPattern) rest = i;
				Add(rest < 0 ? $"{LenText(subj)} == {n}" : $"{LenText(subj)} >= {n - 1}");
				for (int i = 0; i < n; i++)
				{
					var el = dp.Elements[i];
					if (el is BoundRestPattern rp)
					{
						if (rp.Local is not null) binds.Add(new Bind(rp.Local, RestSlice(subj, i, n - 1 - i), false));
						continue;
					}
					string idx = rest >= 0 && i > rest ? $"{LenText(subj)} - {n - i}" : i.ToString();
					Add(PatternCond(el, $"{subj}[{idx}]", binds));
				}
				break;
			}
			case DestructureKind.Object:
				if (!dp.TestType.Equals(dp.Type)) Add(IsType(subj, dp.TestType, false));
				for (int i = 0; i < n && i < dp.Members.Count; i++)
					Add(PatternCond(dp.Elements[i], $"{subj}.{MemberName(dp.Members[i].Name)}", binds));
				break;
			case DestructureKind.ResultOk:
				Add(ResultIsOk(subj));
				if (n > 0) Add(PatternCond(dp.Elements[0], ResultValue(subj), binds));
				break;
			case DestructureKind.ResultErr:
				Add($"!{ResultIsOk(subj)}");
				if (n > 0) Add(PatternCond(dp.Elements[0], ResultError(subj), binds));
				break;
		}
		return conds.Count == 0 ? null : string.Join(" && ", conds);
	}

	// ------------------------------------------------------------------ циклы

	private void LoopStmt(BoundLoop l, Dest d)
	{
		string? acc = null;
		var inner = Dest.Discard;
		if (d.Kind != DestKind.Discard && l.Type is ListType lt)
		{
			acc = Temp("acc");
			DeclareLocal(acc, l.Type, NewListText(lt.Element), true);
			inner = Dest.AddTo(acc);
		}
		var binds = new List<Bind>();
		string variable = LoopVariable(l, binds, out var it);
		bool opened = false;
		if (l.Iterable is BoundRange { Start: { } s, End: { } e } r && l.Kind == LoopKind.Range)
		{
			var step = r.Step is null ? null : Value(r.Step);
			bool neg = r.Step is BoundUnary { Op: Syn.UnaryOp.Negate } or BoundNumberLiteral { Value.Integer.Sign: < 0 };
			OpenForRange(variable, Value(s), Value(e), r.Inclusive, step, neg);
			opened = true;
		}
		if (!opened) OpenForEach(it ?? variable, Value(l.Iterable), l.IsAsync);
		EmitBinds(binds);
		Stmt(l.Body, inner);
		W.Close();
		if (acc is not null) Finish(d, acc, l);
	}

	private string LoopVariable(BoundLoop l, List<Bind> binds, out string? deconstruct)
	{
		deconstruct = null;
		switch (l.Variable)
		{
			case BoundBindingPattern b: return NameOf(b.Local);
			case BoundWildcardPattern: return Temp("_i");
			case BoundDestructurePattern { Kind: DestructureKind.Tuple } dp
				when dp.Elements.All(x => x is BoundBindingPattern or BoundWildcardPattern):
			{
				var names = dp.Elements.Select(x => x is BoundBindingPattern bp ? NameOf(bp.Local) : "_").ToList();
				deconstruct = DeconstructVariable(names);
				return deconstruct;
			}
			default:
			{
				var tmp = Temp("it");
				PatternCond(l.Variable, tmp, binds);
				return tmp;
			}
		}
	}

	private void WhileStmt(BoundWhile w, Dest d)
	{
		string? acc = null;
		var inner = Dest.Discard;
		if (d.Kind != DestKind.Discard && w.Type is ListType lt)
		{
			acc = Temp("acc");
			DeclareLocal(acc, w.Type, NewListText(lt.Element), true);
			inner = Dest.AddTo(acc);
		}
		if (w.Condition is null) { W.Open("while (true)"); Stmt(w.Body, inner); W.Close(); }
		else if (w.PostCondition)
		{
			W.Open("do");
			Stmt(w.Body, inner);
			var c = StripParens(Value(w.Condition));
			CloseDoWhile(c);
		}
		else
		{
			var (c, text) = Capture(() => Value(w.Condition));
			if (text.Length == 0) W.Open($"while ({StripParens(c)})");
			else
			{
				W.Open("while (true)");
				W.Line(text.TrimEnd('\n'));
				W.Open($"if (!({StripParens(c)}))");
				W.Line("break" + End);
				W.Close();
			}
			Stmt(w.Body, inner);
			W.Close();
		}
		if (acc is not null) Finish(d, acc, w);
	}
}
