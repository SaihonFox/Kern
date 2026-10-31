using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Declarations;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Overloads;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding.Core;

/// <summary>Привязка целых тел: функции, инициализаторы полей, вызовы конструкторов родителей, элементы enum.</summary>
public sealed partial class Binder
{
	/// <summary>Привязывает тело функции (метода, конструктора, аксессора, оператора, расширения). Выводит возвращаемый тип, если он не указан.</summary>
	public BoundFunction BindFunction(FunctionSymbol f)
	{
		var type = f.DeclaringType;
		TypeSymbol? self = f.ExtensionOf ?? (type is not null && !f.IsStatic ? type.ThisType : type?.ThisType);
		if (f.IsStatic && f.ExtensionOf is null && type is null) self = null;

		// автосвойство: тело синтезируется
		if (f.Property is { IsAuto: true, BackingField: { } backing } prop && f.Body is null && !f.IsAbstract)
			return SynthesizeAccessor(f, prop, backing, type);

		TypeSymbol? declared = f.IsConstructor ? PrimitiveType.Void : f.ReturnType;
		BeginFunction(type, f, self, f.Parameters, declared, f.IsAsync);

		var chain = f.IsConstructor && type is not null ? BindCtorChain(f, type) : null;
		if (f.IsConstructor && type is { PrimaryConstructor: { } primary } && f != primary && f.Syntax is Syn.CtorDecl { Chain: null } cd)
			Error(Krn.CtorMustChain, $"конструктор типа '{type.Name}' с первичным конструктором должен вызывать другой конструктор: `ctor(...) : $(...)`", cd.Span);

		if (f.Body is null)
			return new BoundFunction(f, f.Parameters, null, f.ReturnType ?? PrimitiveType.Void, false, chain);

		var body = Bind(f.Body, declared);
		bool reaches = Flow.IsReachable;
		TypeSymbol ret;

		if (declared is not null)
		{
			ret = declared;
			if (IsVoid(declared)) { /* значение тела отбрасывается */ }
			else if (body.Type is NeverType || !reaches) { }
			else if (IsVoid(body.Type) && !body.IsError)
				Error(Krn.MissingReturn, $"функция '{f.Name}' должна вернуть '{declared.ToDisplayString()}', но тело не возвращает значения на всех путях", f.Span);
			else
				body = Convert(body, declared, f.Body.Span);
		}
		else
		{
			var candidates = new List<TypeSymbol>(Frame.InferredReturns);
			if (reaches && body.Type is not NeverType) candidates.Add(body.Type);
			ret = UnifyReturnTypes(candidates, f.Span);
			f.ReturnType = ret;
		}
		return new BoundFunction(f, f.Parameters, body, ret, false, chain);
	}

	/// <summary>`: $(args)` — выбор конструктора того же типа (кроме самого себя).</summary>
	private BoundCtorChain? BindCtorChain(FunctionSymbol f, TypeDeclSymbol type)
	{
		if (f.Syntax is not Syn.CtorDecl { Chain: { } chainArgs } decl) return null;
		var ctors = Members.FindConstructors(type.ThisType, out var ownerMap).Where(c => c != f).ToList();
		var call = new Syn.CallExpr(new Syn.IdentExpr("ctor", decl.Span), [], chainArgs, null, decl.Span);
		var args = BindArguments(call, ctors, ownerMap);
		var request = new OverloadRequest(ToCallArguments(args), null, null, ownerMap);
		var result = OverloadResolver.Resolve(ctors, request);
		if (!result.IsSuccess)
		{
			OverloadDiagnostics.Report(Project.Diagnostics, File.File, decl.Span, type.Name, result, request);
			return null;
		}
		var res = result.Selected!;
		return new BoundCtorChain(res.Function, BuildArguments(res, args, decl.Span), res.EvaluationOrder);
	}

	private BoundFunction SynthesizeAccessor(FunctionSymbol f, PropertySymbol prop, FieldSymbol backing, TypeDeclSymbol? type)
	{
		var self = type?.ThisType;
		var span = f.Span;
		BoundExpr? receiver = f.IsStatic || self is null ? null : new BoundThis(self, span);
		var field = new BoundField(receiver, backing, false, prop.Type, span);
		if (f.IsGetter)
			return new BoundFunction(f, f.Parameters, field, prop.Type, IsSynthesized: true);
		var value = new BoundParameter(f.Parameters[0], prop.Type, span);
		return new BoundFunction(f, f.Parameters, new BoundAssignment(field, value, PrimitiveType.Void, span), PrimitiveType.Void, IsSynthesized: true);
	}

	/// <summary>Инициализатор поля/глобальной переменной; выводит тип поля, объявленного через `var`.</summary>
	public BoundExpr? BindFieldInitializer(FieldSymbol field, Syn.Expr? init)
	{
		if (init is null) return null;
		var type = field.DeclaringType;
		TypeSymbol? self = type is not null && !field.IsStatic ? type.ThisType : type?.ThisType;
		BeginFunction(type, null, self, [], PrimitiveType.Void, false);
		var declared = field.Type;
		var value = Bind(init, declared);
		if (declared is not null) return Convert(value, declared, init.Span);
		field.Type = InferStorageType(value, init.Span);
		return value;
	}

	/// <summary>Вызов конструктора родителя из списка наследования: аргументы видят параметры первичного конструктора.</summary>
	public BoundParentInit BindParentInit(TypeDeclSymbol type, ParentInfo parent)
	{
		var primary = type.PrimaryConstructor;
		BeginFunction(type, primary, type.ThisType, primary?.Parameters ?? [], PrimitiveType.Void, false);

		var ctors = Members.FindConstructors(parent.Type, out var ownerMap);
		var call = new Syn.CallExpr(new Syn.IdentExpr("ctor", parent.Syntax.Span), [], parent.Args, null, parent.Syntax.Span);
		var args = BindArguments(call, ctors, ownerMap);

		if (ctors.Count == 0)
		{
			if (args.Count > 0)
				Error(Krn.NoApplicableOverload, $"у родителя '{parent.Type.ToDisplayString()}' нет конструктора с аргументами", parent.Syntax.Span);
			return new BoundParentInit(parent, null, [], null);
		}
		var request = new OverloadRequest(ToCallArguments(args), null, null, ownerMap);
		var result = OverloadResolver.Resolve(ctors, request);
		if (!result.IsSuccess)
		{
			OverloadDiagnostics.Report(Project.Diagnostics, File.File, parent.Syntax.Span, parent.Type.ToDisplayString(), result, request);
			return new BoundParentInit(parent, null, [], null);
		}
		var res = result.Selected!;
		return new BoundParentInit(parent, res.Function, BuildArguments(res, args, parent.Syntax.Span), res.EvaluationOrder);
	}

	/// <summary>Элемент enum: создание значения с аргументами и/или явное числовое значение.</summary>
	public BoundEnumEntry BindEnumEntry(TypeDeclSymbol type, FieldSymbol entry)
	{
		BeginFunction(type, null, type.ThisType, [], PrimitiveType.Void, false);
		BoundNew? construction = null;
		if (entry.EnumArgs.Count > 0 || type.PrimaryConstructor is not null)
		{
			var call = new Syn.CallExpr(new Syn.IdentExpr(entry.Name, entry.Span), [], entry.EnumArgs, null, entry.Span);
			construction = BindConstruct(new TypeResult(type.ThisType, entry.Span, true), call, type.ThisType) as BoundNew;
		}
		BoundExpr? value = entry.Init is null ? null : Bind(entry.Init);
		return new BoundEnumEntry(entry, construction, value);
	}

	/// <summary>Выражение-инициализатор поля из параметра первичного конструктора.</summary>
	public static BoundFieldInitializer ParameterFieldInitializer(FieldSymbol field, ParameterSymbol p) =>
		new(field, new BoundParameter(p, p.EffectiveType, field.Span));
}
