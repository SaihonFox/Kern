using Kern.Bytecode;
using Kern.Compiler.Semantics.Binding.Flow;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Bound.Patterns;
using Kern.Compiler.Semantics.Bound.Statements;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding.Core;

public sealed partial class Binder
{
	// ============================================================ условия и факты

	public BoundExpr BindCondition(Syn.Expr e, string what)
	{
		bool allowNone = e is Syn.IdentExpr || e is Syn.UnaryExpr { Op: Syn.UnaryOp.Not, Operand: Syn.IdentExpr };
		var c = allowNone ? BindAllowingNone(e) : Bind(e);
		return ConvertToBool(c, e.Span, what);
	}

	private static FlowState FlowJoin(FlowState a, FlowState b) => FlowState.Join(a, b);

	/// <summary>Применяет факты условия к состояниям ветвей (null — ветка не интересует).</summary>
	public void ApplyCondition(BoundExpr cond, FlowState? whenTrue, FlowState? whenFalse)
	{
		switch (cond)
		{
			case BoundLocal or BoundParameter when cond.Type is NullableType:
				if (whenTrue is not null) Flow.ApplyHasValue(whenTrue, cond);
				break;

			case BoundTypeTest t:
				if (!t.Negated)
				{
					if (whenTrue is not null) Flow.ApplyIs(whenTrue, t.Operand, t.TestType, positive: true);
					if (whenFalse is not null) Flow.ApplyIs(whenFalse, t.Operand, t.TestType, positive: false);
				}
				else
				{
					if (whenFalse is not null) Flow.ApplyIs(whenFalse, t.Operand, t.TestType, positive: true);
					if (whenTrue is not null) Flow.ApplyIs(whenTrue, t.Operand, t.TestType, positive: false);
				}
				break;

			case BoundUnary { Op: Syn.UnaryOp.Not } u:
				ApplyCondition(u.Operand, whenFalse, whenTrue);
				break;

			case BoundBinary { Op: Syn.BinaryOp.And } a:
				ApplyCondition(a.Left, whenTrue, null);
				ApplyCondition(a.Right, whenTrue, null);
				break;

			case BoundBinary { Op: Syn.BinaryOp.Or } o:
				ApplyCondition(o.Left, null, whenFalse);
				ApplyCondition(o.Right, null, whenFalse);
				break;

			case BoundBinary { Op: Syn.BinaryOp.Eq or Syn.BinaryOp.Ne } eq:
			{
				bool isEq = eq.Op == Syn.BinaryOp.Eq;
				var (value, lit) = eq.Left is BoundNilLiteral or BoundNoneLiteral ? (eq.Right, eq.Left)
					: eq.Right is BoundNilLiteral or BoundNoneLiteral ? (eq.Left, eq.Right) : (null!, null!);
				if (lit is null) break;
				var hasValueBranch = isEq ? whenFalse : whenTrue;   // `x != nil` истинно ⇒ значение есть
				var absentBranch = isEq ? whenTrue : whenFalse;
				if (hasValueBranch is not null)
				{
					if (lit is BoundNilLiteral) Flow.ApplyHasValue(hasValueBranch, value);
					else Flow.ApplyNotNone(hasValueBranch, value);
				}
				_ = absentBranch;
				break;
			}
		}
	}

	// ============================================================ if / ?:

	private BoundExpr BindIf(Syn.IfExpr f, TypeSymbol? expected)
	{
		var cond = BindCondition(f.Condition, "условие `if`");
		return BindBranches(cond, f.Then, f.Else, expected, f.Span);
	}

	private BoundExpr BindTernary(Syn.TernaryExpr t, TypeSymbol? expected)
	{
		var cond = BindCondition(t.Condition, "условие `?:`");
		return BindBranches(cond, t.Then, t.Else, expected, t.Span);
	}

	private BoundExpr BindBranches(BoundExpr cond, Syn.Expr thenSyn, Syn.Expr? elseSyn, TypeSymbol? expected, TextSpan span)
	{
		var thenState = Flow.Fork();
		var elseState = Flow.Fork();
		ApplyCondition(cond, thenState, elseState);

		Flow.Current = thenState;
		var then = Bind(thenSyn, expected);
		var afterThen = Flow.Current;

		Flow.Current = elseState;
		BoundExpr? @else = elseSyn is null ? null : Bind(elseSyn, expected);
		var afterElse = Flow.Current;
		Flow.Current = FlowState.Join(afterThen, afterElse);

		if (@else is null)
			return new BoundIf(cond, then, null, PrimitiveType.Void, span);

		var type = UnifyBranchTypes(then.Type, @else.Type, expected, span);
		if (!type.IsError && !IsVoid(type) && type is not NeverType)
		{
			then = Convert(then, type, thenSyn.Span);
			@else = Convert(@else, type, elseSyn!.Span);
		}
		return new BoundIf(cond, then, @else, type, span);
	}

	private TypeSymbol UnifyBranchTypes(TypeSymbol a, TypeSymbol b, TypeSymbol? expected, TextSpan span)
	{
		if (a.IsError || b.IsError) return ErrorType.Instance;
		if (a is NeverType) return b;
		if (b is NeverType) return a;
		if (IsVoid(a) || IsVoid(b)) return PrimitiveType.Void;
		if (expected is not null && !IsVoid(expected) && Conversions.IsImplicit(a, expected) && Conversions.IsImplicit(b, expected)) return expected;
		var common = Conversions.CommonType(a, b);
		if (common is null)
		{
			Error(Krn.BranchTypeMismatch, $"ветки возвращают несовместимые типы '{a.ToDisplayString()}' и '{b.ToDisplayString()}'", span);
			return ErrorType.Instance;
		}
		return common;
	}

	// ============================================================ блоки и объявления

	private BoundExpr BindBlock(Syn.BlockExpr bl, TypeSymbol? expected)
	{
		PushScope();
		var stmts = new List<BoundStmt>();
		var locals = new List<LocalSymbol>();
		BoundExpr? result = null;
		bool warned = false;
		for (int i = 0; i < bl.Exprs.Count; i++)
		{
			var e = bl.Exprs[i];
			if (!Flow.IsReachable && !warned)
			{
				Warn(Krn.UnreachableCode, "недостижимый код", e.Span);
				warned = true;
			}
			bool isLast = i == bl.Exprs.Count - 1;
			switch (e)
			{
				case Syn.VarDeclExpr vd:
				{
					var decl = BindLocalDeclaration(vd);
					stmts.Add(decl);
					locals.Add(decl.Local);
					break;
				}
				case Syn.DestructureExpr de:
				{
					var d = BindDestructuring(de);
					stmts.Add(d);
					locals.AddRange(d.Locals);
					break;
				}
				default:
				{
					var x = Bind(e, isLast ? expected : null);
					if (isLast) result = x;
					else stmts.Add(new BoundExpressionStatement(x, e.Span));
					break;
				}
			}
		}
		PopScope();
		var type = result?.Type ?? PrimitiveType.Void;
		return new BoundBlock(stmts, result, locals, type, bl.Span);
	}

	private BoundExpr BindDeclarationAsExpression(Syn.Expr e)
	{
		PushScope();
		BoundStmt stmt;
		List<LocalSymbol> locals;
		if (e is Syn.VarDeclExpr vd)
		{
			var decl = BindLocalDeclaration(vd);
			stmt = decl;
			locals = [decl.Local];
		}
		else
		{
			var d = BindDestructuring((Syn.DestructureExpr)e);
			stmt = d;
			locals = [.. d.Locals];
		}
		PopScope();
		return new BoundBlock([stmt], null, locals, PrimitiveType.Void, e.Span);
	}

	public BoundLocalDeclaration BindLocalDeclaration(Syn.VarDeclExpr vd)
	{
		TypeSymbol? declared = vd.Type is null ? null : ResolveType(vd.Type);
		BoundExpr? init = null;
		TypeSymbol? rawInitType = null;   // тип инициализатора до неявного приведения (для smart-cast `T? x = значение T`)

		// локальная функция: имя видно в собственном теле (рекурсия)
		LocalSymbol? early = null;
		if (declared is FuncType && vd.Init is Syn.LambdaExpr && vd.CtorArgs is null)
		{
			early = DeclareLocal(vd.Name, declared, vd.NameSpan, vd.SingleAssign);
			Flow.Declare(early, true);
		}

		if (vd.CtorArgs is not null)
		{
			if (declared is null)
				Error(Krn.CannotInferVariableType, "`var x(args)`: для конструктора нужен явный тип", vd.Span);
			else
			{
				var call = new Syn.CallExpr(new Syn.IdentExpr(vd.Name, vd.NameSpan), [], vd.CtorArgs, null, vd.Span);
				init = BindConstruct(new TypeResult(declared is NullableType nn ? nn.Inner : declared, vd.Span, true), call, declared);
			}
		}
		else if (vd.Init is not null)
		{
			init = Bind(vd.Init, declared);
			rawInitType = init.Type;
			if (declared is not null) init = Convert(init, declared, vd.Init.Span);
		}
		else if (declared is null)
			Error(Krn.CannotInferVariableType, $"у `var {vd.Name}` нет инициализатора — тип вывести нельзя", vd.NameSpan);

		TypeSymbol type = declared ?? (init is null ? ErrorType.Instance : InferStorageType(init, vd.Init?.Span ?? vd.Span));
		if (early is not null) return new BoundLocalDeclaration(early, init, vd.Span);
		var local = DeclareLocal(vd.Name, type, vd.NameSpan, vd.SingleAssign);
		Flow.Declare(local, false);
		if (init is not null) Flow.AssignLocal(local, vd.Span, rawInitType ?? init.Type);
		BoundExpr? dispose = null;
		if (vd.IsUse)
		{
			if (init is null || type is NullableType)
				Error(Krn.MemberNotFound, "`use` требует инициализатор и тип без `?`", vd.Span);
			else if (!Members.FindInstance(type, "dispose", File).Functions.Any(f => f.Parameters.Count == 0))
				Error(Krn.MemberNotFound, $"тип '{type.ToDisplayString()}' не реализует `dispose()` — нельзя использовать с `use`", vd.Span);
			else
			{
				var callee = new Syn.MemberExpr(new Syn.IdentExpr(vd.Name, vd.NameSpan), "dispose", false, vd.Span);
				dispose = Bind(new Syn.CallExpr(callee, [], [], null, vd.Span));
			}
		}
		return new BoundLocalDeclaration(local, init, vd.Span, dispose);
	}

	public BoundDestructuring BindDestructuring(Syn.DestructureExpr de)
	{
		var value = Bind(de.Value);
		var locals = new List<LocalSymbol>();
		var pattern = BindDestructurePattern(de.Target, value.Type, locals, requireIrrefutable: true);
		foreach (var l in locals) Flow.Declare(l, true);
		return new BoundDestructuring(pattern, value, locals, de.Span);
	}

	// ============================================================ stop / next / ret

	private BoundExpr BindStop(TextSpan span)
	{
		if (!Flow.InLoop) return ErrorExpr(Krn.StopOutsideLoop, "`stop` допустим только внутри `loop`", span);
		Flow.Break();
		return new BoundStop(span);
	}

	private BoundExpr BindNext(TextSpan span)
	{
		if (!Flow.InLoop) return ErrorExpr(Krn.NextOutsideLoop, "`next` допустим только внутри `loop`", span);
		Flow.Continue();
		return new BoundNext(span);
	}

	private BoundExpr BindReturn(Syn.ReturnExpr r)
	{
		BoundExpr? value = null;
		var declared = Frame.ReturnType;
		if (declared is not null)
		{
			if (r.Value is null)
			{
				if (!IsVoid(declared) && !declared.IsError)
					Error(Krn.ReturnTypeMismatch, $"`ret` без значения в функции, возвращающей '{declared.ToDisplayString()}'", r.Span);
			}
			else
			{
				value = Bind(r.Value, declared);
				if (IsVoid(declared) && !value.IsError)
					Error(Krn.ReturnTypeMismatch, "функция не возвращает значения (void), а `ret` указывает значение", r.Value.Span);
				else
					value = Convert(value, declared, r.Value.Span);
			}
		}
		else
		{
			value = r.Value is null ? null : Bind(r.Value);
			Frame.InferredReturns.Add(value?.Type ?? PrimitiveType.Void);
		}
		Flow.MarkUnreachable();
		return new BoundReturn(value, r.Span);
	}

	// ============================================================ диапазоны

	private BoundExpr BindRange(Syn.RangeExpr r, TypeSymbol? expected)
	{
		if (r.Start is null && r.End is null)
			return ErrorExpr(Krn.InvalidPattern, "диапазон должен иметь хотя бы одну границу", r.Span);
		var start = r.Start is null ? null : Bind(r.Start);
		var end = r.End is null ? null : Bind(r.End);
		var step = r.Step is null ? null : Bind(r.Step);
		if (start is { IsError: true } || end is { IsError: true } || step is { IsError: true })
			return new BoundRange(start, end, r.Inclusive, step, ErrorType.Instance, ErrorType.Instance, r.Span);

		TypeSymbol elem;
		if (start is not null && end is not null)
		{
			if (Prim(start.Type) is { Kind: PrimKind.Char } && Prim(end.Type) is { Kind: PrimKind.Char }) elem = PrimitiveType.Char;
			else if (TryUnifyNumeric(ref start, ref end, out var common)) elem = common;
			else return ErrorExpr(Krn.TypeMismatch, $"границы диапазона несовместимы: '{start.Type.ToDisplayString()}' и '{end.Type.ToDisplayString()}'", r.Span);
		}
		else elem = (start ?? end)!.Type;

		if (elem is PrimitiveType { IsUniversalNumeric: true } || (!(elem is PrimitiveType { IsNumeric: true } or PrimitiveType { Kind: PrimKind.Char })))
		{
			if (elem is not PrimitiveType { IsNumeric: true } and not PrimitiveType { Kind: PrimKind.Char })
				return ErrorExpr(Krn.TypeMismatch, $"диапазон по значениям типа '{elem.ToDisplayString()}' не поддерживается", r.Span);
		}
		if (start is not null) start = Convert(start, elem, start.Span);
		if (end is not null) end = Convert(end, elem, end.Span);
		if (step is not null) step = Convert(step, elem, step.Span);

		return new BoundRange(start, end, r.Inclusive, step, elem, RangeTypeOf(elem), r.Span);
	}

	private TypeSymbol RangeTypeOf(TypeSymbol elem)
	{
		var nt = Project.Binder.Source.FindType("kern.collections", "range");
		return nt is null ? new ListType(elem) : Project.Binder.TypeOf(nt, [elem]);
	}

	// ============================================================ loop

	private BoundExpr BindLoop(Syn.LoopExpr l)
	{
		var iterable = Bind(l.Iterable);
		if (iterable.IsError)
		{
			PushScope();
			var errLocals = new List<LocalSymbol>();
			var errPattern = BindLoopVariable(l.Variable, ErrorType.Instance, errLocals);
			var errBody = Bind(l.Body);
			PopScope();
			return new BoundLoop(errPattern, iterable, errBody, LoopKind.Custom, ErrorType.Instance, null, false, ErrorType.Instance, l.Span);
		}

		var kind = LoopKind.Custom;
		TypeSymbol elem = ErrorType.Instance;
		FunctionSymbol? iterator = null;
		var it = iterable.Type is NullableType n ? n.Inner : iterable.Type;
		if (iterable.Type is NullableType)
			Error(Krn.NotIterable, $"значение типа '{iterable.Type.ToDisplayString()}' может быть nil", l.Iterable.Span);

		bool isAsync = l.IsAsync;
		if (isAsync)
		{
			if (!Frame.IsAsync)
				Error(Krn.AsyncLoopOutsideAsync, "`async loop` допустим только внутри async-функции", l.Span);
			// тип обязан наследовать async_iter<T> (метод `next() -> task<T?>`; nil завершает поток)
			if (FindStreamBase(it, "async_iter", out var streamElem) && FindNext(it) is { } fn)
			{
				iterator = fn;
				elem = streamElem;
			}
			else
				Error(Krn.NotAsyncStream, $"по значению типа '{iterable.Type.ToDisplayString()}' нельзя итерироваться через `async loop`: тип должен наследовать async_iter<T>", l.Iterable.Span);
		}
		else switch (it)
		{
			case ListType lt: kind = LoopKind.List; elem = lt.Element; break;
			case ArrayType at: kind = LoopKind.Array; elem = at.Element; break;
			case DictType dt: kind = LoopKind.Dict; elem = new TupleType([dt.Key, dt.Value], ["key", "value"]); break;
			case PrimitiveType { Kind: PrimKind.Str }: kind = LoopKind.Custom; elem = PrimitiveType.Char; break;
			case NativeType { Symbol: { Name: "range", Module: "kern.collections" } } rt when rt.Args.Count == 1:
				kind = LoopKind.Range; elem = rt.Args[0]; break;
			default:
			{
				if (iterable is BoundRange br) { kind = LoopKind.Range; elem = br.ElementType; break; }
				// тип, наследующий iter<T>: цикл вызывает `next() -> T?` до nil
				if (FindStreamBase(it, "iter", out var iterElem) && FindNext(it) is { } nextFn)
				{
					kind = LoopKind.Stream; elem = iterElem; iterator = nextFn;
					break;
				}
				var ms = Members.FindInstance(it, "iter", File);
				var fn = ms.Functions.FirstOrDefault(f => f.Parameters.Count == 0 && f.ReturnType is not null);
				if (fn is not null && fn.ReturnType!.Substitute(ms.OwnerMap) is ListType or ArrayType or DictType)
				{
					iterator = fn;
					var rtype = fn.ReturnType.Substitute(ms.OwnerMap);
					elem = rtype switch { ListType x => x.Element, ArrayType y => y.Element, DictType d => new TupleType([d.Key, d.Value], ["key", "value"]), _ => ErrorType.Instance };
					break;
				}
				Error(Krn.NotIterable, $"по значению типа '{iterable.Type.ToDisplayString()}' нельзя итерироваться (нужен list, array, dict, range, str, тип-наследник iter<T> или метод `iter()`)", l.Iterable.Span);
				break;
			}
		}
		if (!isAsync && iterable is BoundRange rr) { kind = LoopKind.Range; elem = rr.ElementType; }

		PushScope();
		var locals = new List<LocalSymbol>();
		var variable = BindLoopVariable(l.Variable, elem, locals);
		foreach (var local in locals) Flow.Declare(local, true);

		var pre = Flow.Fork();
		Flow.EnterLoop(AssignedNames.In(l.Body));
		var body = Bind(l.Body);
		var endOfBody = Flow.Current;
		var breaks = Flow.ExitLoop();
		PopScope();
		Flow.Current = FlowState.Join([pre, endOfBody, .. breaks]);
		if (!Flow.Current.Reachable) Flow.Current = pre;

		TypeSymbol result = body.Type is NeverType || IsVoid(body.Type) || body.IsError ? PrimitiveType.Void : new ListType(body.Type);
		return new BoundLoop(variable, iterable, body, kind, elem, iterator, isAsync, result, l.Span);
	}

	private BoundExpr BindWhile(Syn.WhileExpr w)
	{
		var pre = Flow.Fork();
		// присваиваемое в теле и условии сбрасывает сужение: на следующей итерации оно неверно
		Flow.EnterLoop(AssignedNames.In(w));

		if (w.PostCondition && w.Condition is { } postCond)
		{
			// do-while: тело, затем условие; выходы — `stop` и ложное условие
			var postBody = Bind(w.Body);
			var postC = BindCondition(postCond, "условие `loop … :`");
			var wt = Flow.Fork();
			var wf = Flow.Fork();
			ApplyCondition(postC, wt, wf);
			var postBreaks = Flow.ExitLoop();
			Flow.Current = FlowState.Join([.. postBreaks, wf]);
			if (!Flow.Current.Reachable) Flow.Current = pre;
			TypeSymbol postResult = postBody.Type is NeverType || IsVoid(postBody.Type) || postBody.IsError ? PrimitiveType.Void : new ListType(postBody.Type);
			return new BoundWhile(postC, postBody, postResult, w.Span, true);
		}

		BoundExpr? cond = null;
		FlowState? whenFalse = null;
		if (w.Condition is { } cs)
		{
			cond = BindCondition(cs, "условие `loop`");
			var whenTrue = Flow.Fork();
			whenFalse = Flow.Fork();
			ApplyCondition(cond, whenTrue, whenFalse);
			Flow.Current = whenTrue;
		}

		var body = Bind(w.Body);
		var breaks = Flow.ExitLoop();

		var exits = new List<FlowState>(breaks);
		if (whenFalse is not null) exits.Add(whenFalse);
		Flow.Current = exits.Count > 0 ? FlowState.Join(exits) : pre;
		if (!Flow.Current.Reachable) Flow.Current = pre;

		TypeSymbol result = body.Type is NeverType || IsVoid(body.Type) || body.IsError ? PrimitiveType.Void : new ListType(body.Type);
		return new BoundWhile(cond, body, result, w.Span);
	}

	/// <summary>Находит среди самого типа и его предков `async_iter<T>` / `iter<T>` из std; возвращает T.</summary>
	private static bool FindStreamBase(TypeSymbol type, string baseName, out TypeSymbol element)
	{
		foreach (var t in new[] { type }.Concat(Conversions.BaseTypes(type)))
			if (t is DeclaredType { Args.Count: 1 } d && d.Decl.Name == baseName)
			{
				element = d.Args[0];
				return true;
			}
		element = ErrorType.Instance;
		return false;
	}

	private FunctionSymbol? FindNext(TypeSymbol type) =>
		Members.FindInstance(type, "next", File).Functions.FirstOrDefault(f => f.Parameters.Count == 0);

	private BoundPattern BindLoopVariable(Syn.Pattern p, TypeSymbol elem, List<LocalSymbol> locals) =>
		p switch
		{
			Syn.DestructurePattern d => BindDestructurePattern(d, elem, locals, requireIrrefutable: true),
			_ => BindPattern(p, elem, locals, requireIrrefutable: true),
		};
}
