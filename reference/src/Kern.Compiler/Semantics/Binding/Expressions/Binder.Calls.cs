using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Overloads;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding.Core;

public sealed partial class Binder
{
	private sealed record BoundArg(string? Name, BoundExpr Value, TextSpan Span);

	private BoundExpr BindCall(Syn.CallExpr c, TypeSymbol? expected)
	{
		if (TryBindMeta(c, out var meta)) return meta;
		BindResult callee = c.Callee is Syn.IdentExpr or Syn.MemberExpr or Syn.ParentMemberExpr or Syn.SelfMemberExpr
			? BindTarget(c.Callee, c.TypeArgs)
			: new ValueResult(Bind(c.Callee));

		switch (callee)
		{
			case GroupResult { IsConstructorGroup: true } g:
				return BindResultCreate(g.Name == "Ok", c, expected);
			case GroupResult g:
				return BindGroupCall(g, c, expected);
			case TypeResult t:
				return BindConstruct(t, c, expected);
			case ValueResult { Value: var v }:
				return BindDelegateCall(v, c);
			case ModuleResult m:
				return ErrorExpr(Krn.NotCallable, $"'{m.Path}' — модуль, его нельзя вызвать", c.Span);
			default:
				return new BoundErrorExpr(c.Span);
		}
	}

	// ------------------------------------------------------------ аргументы

	private List<BoundArg> BindArguments(Syn.CallExpr c, IReadOnlyList<FunctionSymbol> candidates, IReadOnlyDictionary<TypeParamSymbol, TypeSymbol>? ownerMap)
	{
		// подсказка ожидаемых типов: берётся из кандидатов, подходящих по числу аргументов; если они согласны о типе параметра — подсказка есть
		// (так `db.exec(sql, [1, "a"])` получает ожидаемый `list<object>` даже при нескольких перегрузках)
		int argCount = c.Args.Count + (c.TrailingLambda is null ? 0 : 1);
		var fitting = candidates.Where(f => argCount <= f.Parameters.Count || f.Parameters.Any(p => p.IsVariadic)).ToList();
		TypeSymbol? HintFor(int position, string? name)
		{
			TypeSymbol? hint = null;
			foreach (var cand in fitting)
			{
				ParameterSymbol? p = name is not null
					? cand.Parameters.FirstOrDefault(x => x.Name == name)
					: position < cand.Parameters.Count ? cand.Parameters[position] : null;
				if (p is null) continue;
				var t = ownerMap is { Count: > 0 } ? p.EffectiveType.Substitute(ownerMap) : p.EffectiveType;
				if (t.ContainsTypeParam || p.IsVariadic) return null;
				if (hint is null) hint = t;
				else if (!hint.Equals(t)) return null;
			}
			return hint;
		}

		var list = new List<BoundArg>();
		int pos = 0;
		foreach (var a in c.Args)
		{
			var bound = Bind(a.Value, HintFor(pos, a.Name));
			list.Add(new BoundArg(a.Name, bound, a.Span));
			if (a.Name is null) pos++;
		}
		if (c.TrailingLambda is { } tl)
			list.Add(new BoundArg(null, Bind(tl, HintFor(pos, null)), tl.Span));
		return list;
	}

	private static List<CallArgument> ToCallArguments(IReadOnlyList<BoundArg> args) =>
		[.. args.Select(a => new CallArgument(a.Value.Type, a.Span, a.Name, a.Value is BoundNumberLiteral { Value.Suffix: null } n ? n.Value : null))];

	// ------------------------------------------------------------ вызов группы функций

	private BoundExpr BindGroupCall(GroupResult g, Syn.CallExpr c, TypeSymbol? expected)
	{
		var args = BindArguments(c, g.Candidates, g.OwnerMap);
		var typeArgs = c.TypeArgs.Select(ResolveType).ToList();
		return ResolveGroupCall(g, args, typeArgs, c.Span);
	}

	private BoundExpr ResolveGroupCall(GroupResult g, List<BoundArg> args, IReadOnlyList<TypeSymbol> typeArgs, TextSpan span)
	{
		TypeSymbol? receiverType = g.Receiver?.Type is { } rt ? (rt is NullableType n ? n.Inner : rt) : null;
		var request = new OverloadRequest(ToCallArguments(args), typeArgs.Count > 0 ? typeArgs : null, receiverType, g.OwnerMap);
		if (Owner is not null)
			foreach (var cand in g.Candidates) Owner.EnsureSignature(cand);

		var direct = g.Candidates.Where(f => !f.IsExtension).ToList();
		var extensions = g.Candidates.Where(f => f.IsExtension).ToList();
		var result = OverloadResolver.ResolveTiers([direct, extensions], request);
		if (!result.IsSuccess)
		{
			OverloadDiagnostics.Report(Project.Diagnostics, File.File, span, g.Name, result, request);
			return new BoundErrorExpr(span);
		}
		return BuildCall(result.Selected!, g, args, span);
	}

	private BoundExpr BuildCall(ResolvedCall res, GroupResult g, List<BoundArg> args, TextSpan span)
	{
		var f = res.Function;
		if (g.Receiver is null && !f.IsStatic && !f.IsExtension && f.DeclaringType is not null && !f.IsConstructor)
			return ErrorExpr(Krn.InstanceMemberInStaticContext, $"метод '{f.Name}' требует экземпляр типа '{f.DeclaringType.Name}'", span);

		CheckAccess(f, span);
		var arguments = BuildArguments(res, args, span);
		BoundExpr? receiver = g.Receiver;
		if (receiver is not null && res.ReceiverConversion is { NeedsNode: true } && res.ReceiverTarget is { } rtarget && !(receiver.Type is NullableType))
			receiver = Convert(receiver, rtarget, receiver.Span);

		TypeSymbol ret = res.ReturnType;
		if (f.IsAsync && !f.IsNative) ret = new TaskType(ret);
		if (g.IsSafe && !IsVoid(ret) && ret is not NeverType) ret = NullableType.Of(ret);

		return new BoundCall(f, receiver, res.TypeArgs, res.OwnerTypeArgs, arguments, res.EvaluationOrder, g.IsSafe,
			IsNonVirtual: receiver is BoundParentThis, ret, span);
	}

	/// <summary>Позиционный список аргументов по слотам параметров: приведения, значения по умолчанию, упаковка variadic.</summary>
	private List<BoundExpr> BuildArguments(ResolvedCall res, IReadOnlyList<BoundArg> args, TextSpan span)
	{
		var result = new List<BoundExpr>(res.Slots.Count);
		foreach (var slot in res.Slots)
		{
			switch (slot.Kind)
			{
				case ParameterSlotKind.Argument:
				{
					var a = args[slot.ArgumentIndices[0]];
					result.Add(Convert(a.Value, slot.ParameterType, a.Span));
					break;
				}
				case ParameterSlotKind.Default:
					result.Add(MakeDefaultArgument(slot, span));
					break;
				case ParameterSlotKind.VariadicPack:
				{
					var elemType = slot.Parameter.Type.Substitute(res.Substitution);
					var elems = slot.ArgumentIndices.Select(i => Convert(args[i].Value, elemType, args[i].Span)).ToList();
					result.Add(new BoundList(elems, new ListType(elemType), span));
					break;
				}
			}
		}
		return result;
	}

	private BoundExpr MakeDefaultArgument(ParameterSlot slot, TextSpan span)
	{
		var p = slot.Parameter;
		BoundExpr? value = null;
		if (p.Default is { } d)
		{
			// значение по умолчанию привязывается в контексте вызова (допущение: оно не зависит от локальных имён объявляющей функции)
			value = Convert(Bind(d, slot.ParameterType), slot.ParameterType, d.Span);
		}
		return new BoundDefaultArgument(p, value, slot.ParameterType, span);
	}

	// ------------------------------------------------------------ вызов значения-функции

	private BoundExpr BindDelegateCall(BoundExpr callee, Syn.CallExpr c)
	{
		if (callee.IsError)
		{
			foreach (var a in c.Args) Bind(a.Value);
			return callee;
		}
		var type = callee.Type is NullableType nt ? nt.Inner : callee.Type;
		if (type is not FuncType ft)
		{
			foreach (var a in c.Args) Bind(a.Value);
			return ErrorExpr(Krn.NotCallable, $"значение типа '{callee.Type.ToDisplayString()}' нельзя вызвать", c.Span);
		}
		if (callee.Type is NullableType)
			return ErrorExpr(Krn.NotCallable, $"функция типа '{callee.Type.ToDisplayString()}' может быть nil; проверьте перед вызовом", c.Span);

		var args = new List<BoundExpr>();
		var all = c.Args.ToList();
		int total = all.Count + (c.TrailingLambda is null ? 0 : 1);
		if (all.Any(a => a.Name is not null))
			return ErrorExpr(Krn.DelegateCallMismatch, "при вызове значения-функции именованные аргументы не поддерживаются", c.Span);
		if (total != ft.Params.Count)
		{
			foreach (var a in c.Args) Bind(a.Value);
			return ErrorExpr(Krn.DelegateCallMismatch,
				$"функция '{ft.ToDisplayString()}' принимает аргументов: {ft.Params.Count}, передано: {total}", c.Span);
		}
		for (int i = 0; i < all.Count; i++)
			args.Add(Convert(Bind(all[i].Value, ft.Params[i]), ft.Params[i], all[i].Span));
		if (c.TrailingLambda is { } tl)
			args.Add(Convert(Bind(tl, ft.Params[^1]), ft.Params[^1], tl.Span));
		return new BoundDelegateCall(callee, args, false, ft.Return, c.Span);
	}

	// ------------------------------------------------------------ конструкторы

	private BoundExpr BindConstruct(TypeResult t, Syn.CallExpr c, TypeSymbol? expected)
	{
		var type = t.Type;
		if (type.IsError)
		{
			foreach (var a in c.Args) Bind(a.Value);
			return new BoundErrorExpr(c.Span);
		}
		if (type is DeclaredType { Decl: { IsAbstract: true } })
			return ErrorExpr(Krn.AbstractInstantiation, $"абстрактный тип '{type.ToDisplayString()}' нельзя создать", c.Span);
		if (type is UnionType or EnumType)
			return ErrorExpr(Krn.NotCallable, $"'{type.ToDisplayString()}' нельзя создать через вызов конструктора", c.Span);

		var ctors = Members.FindConstructors(type, out var ownerMap);
		var args = BindArguments(c, ctors, ownerMap);

		// вывод generic-аргументов класса по аргументам конструктора: `Box(5)`
		if (!t.HasExplicitArgs && TryGetOpenTypeParams(type, out var openParams))
		{
			var inferred = InferClassArguments(type, openParams, ctors, args, expected, out var map);
			if (inferred is null)
			{
				OverloadDiagnostics.Report(Project.Diagnostics, File.File, c.Span, type.ToDisplayString(),
					new OverloadResult { Status = OverloadStatus.NoApplicable, SuppressedByErrorArguments = args.Any(a => a.Value.IsError) },
					new OverloadRequest(ToCallArguments(args)));
				if (!args.Any(a => a.Value.IsError))
					Error(Krn.CannotInferTypeArgs, $"не удалось вывести generic-аргументы типа '{type.ToDisplayString()}': укажите их явно `<...>`", c.Span);
				return new BoundErrorExpr(c.Span);
			}
			type = inferred;
			ownerMap = map;
		}

		if (ctors.Count == 0)
		{
			if (args.Count > 0)
				return ErrorExpr(Krn.NoApplicableOverload, $"у типа '{type.ToDisplayString()}' нет конструктора с аргументами", c.Span);
			return new BoundNew(null, TypeArgsOf(type), [], null, type, c.Span);
		}

		var request = new OverloadRequest(ToCallArguments(args), null, null, ownerMap);
		var result = OverloadResolver.Resolve(ctors, request);
		if (!result.IsSuccess)
		{
			OverloadDiagnostics.Report(Project.Diagnostics, File.File, c.Span, type.ToDisplayString(), result, request);
			return new BoundErrorExpr(c.Span);
		}
		var res = result.Selected!;
		CheckAccess(res.Function, c.Span);
		var arguments = BuildArguments(res, args, c.Span);
		return new BoundNew(res.Function, TypeArgsOf(type), arguments, res.EvaluationOrder, type, c.Span);
	}

	private static IReadOnlyList<TypeSymbol> TypeArgsOf(TypeSymbol t) => t switch
	{
		DeclaredType d => d.Args,
		NativeType n => n.Args,
		BuiltinGenericType b => b.Args,
		_ => [],
	};

	private bool TryGetOpenTypeParams(TypeSymbol type, out IReadOnlyList<TypeParamSymbol> ps)
	{
		ps = [];
		switch (type)
		{
			case DeclaredType { Decl.TypeParams.Count: > 0 } d when d.Args.Count == d.Decl.TypeParams.Count
				&& d.Args.Select((a, i) => a is TypeParamType tp && ReferenceEquals(tp.Param, d.Decl.TypeParams[i])).All(x => x):
				ps = d.Decl.TypeParams;
				return true;
			case NativeType { Args.Count: > 0 } n:
			{
				var list = Project.Binder.TypeParamsOf(n.Symbol);
				if (n.Args.Count == list.Count && n.Args.Select((a, i) => a is TypeParamType tp && ReferenceEquals(tp.Param, list[i])).All(x => x))
				{
					ps = list;
					return true;
				}
				return false;
			}
		}
		return false;
	}

	private TypeSymbol? InferClassArguments(
		TypeSymbol type, IReadOnlyList<TypeParamSymbol> typeParams, IReadOnlyList<FunctionSymbol> ctors, List<BoundArg> args,
		TypeSymbol? expected, out IReadOnlyDictionary<TypeParamSymbol, TypeSymbol> map)
	{
		foreach (var ctor in ctors)
		{
			var inferrer = new TypeInferrer(typeParams);
			int pos = 0;
			foreach (var a in args)
			{
				ParameterSymbol? p = a.Name is not null
					? ctor.Parameters.FirstOrDefault(x => x.Name == a.Name)
					: pos < ctor.Parameters.Count ? ctor.Parameters[pos] : null;
				if (a.Name is null) pos++;
				if (p is not null) inferrer.InferFrom(p.IsVariadic ? p.Type : p.Type, a.Value.Type);
			}
			var fixedMap = new Dictionary<TypeParamSymbol, TypeSymbol>();
			if (inferrer.TryFix(fixedMap, out _))
			{
				map = fixedMap;
				return type.Substitute(fixedMap);
			}
		}
		// подсказка из ожидаемого типа (`Box<i32> b = Box()`)
		if (expected is DeclaredType ed && type is DeclaredType td && ReferenceEquals(ed.Decl, td.Decl))
		{
			map = ed.TypeMap();
			return ed;
		}
		map = new Dictionary<TypeParamSymbol, TypeSymbol>();
		return null;
	}

	// ------------------------------------------------------------ Ok / Err

	private BoundExpr BindResultCreate(bool isOk, Syn.CallExpr c, TypeSymbol? expected)
	{
		string name = isOk ? "Ok" : "Err";
		var er = expected as ResultType;
		if (c.Args.Count > 1 || c.TrailingLambda is not null || c.Args.Any(a => a.Name is not null))
			return ErrorExpr(Krn.ArgumentMismatch, $"`{name}` принимает не более одного позиционного аргумента", c.Span);
		if (c.Args.Count == 0)
		{
			if (!isOk) return ErrorExpr(Krn.ArgumentMismatch, "`Err` требует значение ошибки", c.Span);
			var t = er ?? new ResultType(PrimitiveType.Void, NeverType.Instance);
			return new BoundResultCreate(true, null, t, c.Span);
		}

		var hint = er is null ? null : isOk ? er.Ok : er.Err;
		var value = Bind(c.Args[0].Value, hint);
		if (hint is not null) value = Convert(value, hint, c.Args[0].Span);
		if (value.IsError) return new BoundResultCreate(isOk, value, ErrorType.Instance, c.Span);

		TypeSymbol type = er ?? (isOk
			? new ResultType(InferStorageType(value, c.Span), NeverType.Instance)
			: new ResultType(NeverType.Instance, InferStorageType(value, c.Span)));
		return new BoundResultCreate(isOk, value, type, c.Span);
	}

	// ------------------------------------------------------------ await

	private BoundExpr BindAwait(Syn.AwaitExpr a)
	{
		var operand = Bind(a.Operand);
		if (!Frame.IsAsync)
			Error(Krn.AwaitOutsideAsync, "`await` допустим только внутри async-функции или async-лямбды", a.Span);
		if (operand.IsError) return new BoundAwait(operand, ErrorType.Instance, a.Span);
		if (operand.Type is TaskType tt)
			return new BoundAwait(operand, tt.Result, a.Span);
		return new BoundAwait(ErrorExpr(Krn.AwaitNotTask, $"`await` применим к task<T>, получено '{operand.Type.ToDisplayString()}'", a.Span), ErrorType.Instance, a.Span);
	}
}
