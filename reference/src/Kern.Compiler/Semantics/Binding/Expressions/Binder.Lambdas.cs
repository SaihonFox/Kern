using Kern.Compiler.Semantics.Binding.Flow;
using Kern.Compiler.Semantics.Scopes;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding.Core;

public sealed partial class Binder
{
	private BoundLambda BindLambda(Syn.LambdaExpr lam, TypeSymbol? expected)
	{
		var declaredReturn = lam.ReturnType is null ? null : ResolveType(lam.ReturnType);
		var frame = new FunctionFrame(Frame, true)
		{
			IsAsync = lam.IsAsync,
			ReturnType = declaredReturn,
			Description = "лямбда",
		};

		var parameters = new List<ParameterSymbol>();
		for (int i = 0; i < lam.Params.Count; i++)
		{
			var p = lam.Params[i];
			var type = ResolveType(p.Type);
			parameters.Add(new ParameterSymbol(p.Name, type, i) { IsVariadic = p.IsVariadic, File = File.File, Span = p.NameSpan });
		}

		// вход: новый кадр/область, независимое «окно» состояния потока, `stop`/`next` внешнего цикла недоступны
		var oldFrame = Frame;
		var oldScope = Scope;
		var oldState = Flow.Current;
		var loops = Flow.HideLoops();

		Frame = frame;
		Scope = new Scope(oldScope, "lambda");
		frame.RootScope = Scope;
		foreach (var p in parameters) DeclareParameter(p);

		var entry = oldState.Clone();
		entry.ClearNarrowingByName(AssignedNames.In(lam.Body));
		Flow.Current = entry;
		foreach (var p in parameters) Flow.Current.ResetVariable(p);

		TypeSymbol? hint = declaredReturn ?? (expected is FuncType { Return: { ContainsTypeParam: false } r } && !IsVoid(r) ? r : null);
		var body = Bind(lam.Body, hint);
		var bodyReaches = Flow.IsReachable;

		TypeSymbol ret;
		if (declaredReturn is not null)
		{
			ret = declaredReturn;
			if (!IsVoid(declaredReturn) && body.Type is not NeverType && bodyReaches)
				body = Convert(body, declaredReturn, lam.Body.Span);
		}
		else
		{
			var candidates = new List<TypeSymbol>(frame.InferredReturns);
			if (bodyReaches && body.Type is not NeverType) candidates.Add(body.Type);
			ret = UnifyReturnTypes(candidates, lam.Span);
		}

		// выход
		var captures = frame.Captures.Values
			.Select(c => new BoundCapture(c.Variable, c.Type, c.WrittenInside, c.FirstUse)).ToList();
		bool capturesThis = frame.CapturesThis;
		Frame = oldFrame;
		Scope = oldScope;
		Flow.Current = oldState;
		Flow.RestoreLoops(loops);

		var funcType = new FuncType([.. parameters.Select(p => p.EffectiveType)], lam.IsAsync ? new TaskType(ret) : ret);
		return new BoundLambda(parameters, body, captures, capturesThis, lam.IsAsync, funcType, lam.Span);
	}

	private TypeSymbol UnifyReturnTypes(IReadOnlyList<TypeSymbol> types, TextSpan span)
	{
		TypeSymbol? acc = null;
		foreach (var t in types)
		{
			if (t.IsError) return ErrorType.Instance;
			if (acc is null) { acc = t; continue; }
			if (IsVoid(acc) != IsVoid(t))
			{
				Error(Krn.CannotInferReturnType, "ветки возвращают то значение, то ничего: тип возврата вывести нельзя", span);
				return ErrorType.Instance;
			}
			var common = IsVoid(acc) ? acc : Conversions.CommonType(acc, t);
			if (common is null)
			{
				Error(Krn.CannotInferReturnType, $"возвращаются несовместимые типы '{acc.ToDisplayString()}' и '{t.ToDisplayString()}'", span);
				return ErrorType.Instance;
			}
			acc = common;
		}
		return acc ?? PrimitiveType.Void;
	}
}
