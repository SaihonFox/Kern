using Kern.Binding.Catalog;
using Kern.Compiler.Semantics.Binding.Members;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding.Core;

public sealed partial class Binder
{
	private bool _allowNone;
	private bool _noRead;

	private void NoteThisUse()
	{
		for (var f = Frame; f is not null; f = f.Parent) f.CapturesThis = true;
	}

	/// <summary>Привязывает операнд, для которого допустимо состояние `none` (`x == none`, `x ?: y`).</summary>
	private BoundExpr BindAllowingNone(Syn.Expr e, TypeSymbol? expected = null)
	{
		bool saved = _allowNone;
		_allowNone = true;
		try { return Bind(e, expected); }
		finally { _allowNone = saved; }
	}

	// ------------------------------------------------------------ цели

	public BindResult BindTarget(Syn.Expr e, IReadOnlyList<Syn.TypeRef> typeArgs)
	{
		switch (e)
		{
			case Syn.IdentExpr id:
				return BindIdent(id, typeArgs);
			case Syn.MemberExpr m:
				return BindMember(m, typeArgs);
			case Syn.SelfExpr s:
				if (!HasThis) return new ValueResult(ErrorExpr(Krn.ThisUnavailable, "`$` недоступен в этом контексте", s.Span));
				NoteThisUse();
				return new ValueResult(new BoundThis(SelfType!, s.Span));
			case Syn.SelfMemberExpr sm:
			{
				if (!HasThis) return new ValueResult(ErrorExpr(Krn.ThisUnavailable, $"`${sm.Name}` недоступен в этом контексте", sm.Span));
				NoteThisUse();
				return MemberOfValue(new BoundThis(SelfType!, sm.Span), sm.Name, false, sm.Span);
			}
			case Syn.ParentMemberExpr pm:
				return BindParentMember(pm);
			default:
				return new ValueResult(Bind(e));
		}
	}

	/// <summary>Приводит результат привязки к значению (тип/модуль/группа как значение — ошибка либо ссылка на функцию).</summary>
	public BoundExpr ToValue(BindResult r, TypeSymbol? expected = null)
	{
		switch (r)
		{
			case ValueResult v:
				return v.Value;
			case TypeResult t:
				return ErrorExpr(Krn.NotAValue, $"'{t.Type.ToDisplayString()}' — тип, а не значение", t.Span);
			case ModuleResult m:
				return ErrorExpr(Krn.NotAValue, $"'{m.Path}' — модуль, а не значение", m.Span);
			case GroupResult g:
				return GroupAsFunctionRef(g, expected);
			default:
				return new BoundErrorExpr(r.Span);
		}
	}

	private BoundExpr GroupAsFunctionRef(GroupResult g, TypeSymbol? expected)
	{
		var candidates = g.Candidates.ToList();
		if (expected is FuncType ft)
		{
			candidates = [.. candidates.Where(c => c.Parameters.Count == ft.Params.Count
				&& c.Parameters.Select((p, i) => p.Type.ContainsTypeParam || Conversions.IsImplicit(ft.Params[i], p.Type)).All(x => x))];
		}
		if (candidates.Count == 0) candidates = [.. g.Candidates];
		if (candidates.Count != 1)
			return ErrorExpr(Krn.AmbiguousFunctionRef, $"'{g.Name}' — группа перегрузок ({candidates.Count}); укажите нужную через ожидаемый тип функции", g.Span);
		var f = candidates[0];
		if (g.Receiver is null && !f.IsStatic && f.DeclaringType is not null && !f.IsConstructor && f.ExtensionOf is null)
			return ErrorExpr(Krn.InstanceMemberInStaticContext, $"метод '{f.Name}' требует экземпляр", g.Span);
		var map = g.OwnerMap;
		TypeSymbol type = f.AsFuncType();
		if (map is { Count: > 0 }) type = type.Substitute(map);
		return new BoundFunctionRef(f, g.Receiver, [], type, g.Span);
	}

	// ------------------------------------------------------------ идентификаторы

	private BindResult BindIdent(Syn.IdentExpr id, IReadOnlyList<Syn.TypeRef> typeArgs)
	{
		string name = id.Name;
		var span = id.Span;
		if (name == "_")
			return new ValueResult(ErrorExpr(Krn.NotAValue, "`_` нельзя использовать как значение", span));

		var sym = Scope.Lookup(name);

		switch (sym)
		{
			case LocalSymbol local:
			{
				var type = _noRead ? local.Type ?? ErrorType.Instance : Flow.TypeOf(local);
				if (!_noRead) Flow.ReadLocal(local, span, _allowNone);
				NoteUse(local, local.Type ?? type, span, written: _noRead);
				return new ValueResult(new BoundLocal(local, type, span));
			}
			case ParameterSymbol p:
				NoteUse(p, p.EffectiveType, span, written: _noRead);
				return new ValueResult(new BoundParameter(p, _noRead ? p.EffectiveType : Flow.TypeOf(p), span));
		}

		// члены текущего типа (неявный this и статические члены), но не расширения
		if (SelfType is not null)
		{
			if (HasThis)
			{
				var inst = Members.FindInstance(SelfType, name, File, includeExtensions: false);
				if (!inst.IsEmpty)
				{
					NoteThisUse();
					return FromMemberSet(inst, new BoundThis(SelfType, span), SelfType, name, false, span);
				}
			}
			var stat = Members.FindStatic(SelfType, name);
			if (!stat.IsEmpty) return FromStaticSet(stat, SelfType, name, span);
		}

		return FromSymbol(sym, name, typeArgs, span, id);
	}

	private BindResult FromSymbol(Symbol? sym, string name, IReadOnlyList<Syn.TypeRef> typeArgs, TextSpan span, Syn.Expr origin)
	{
		switch (sym)
		{
			case FieldSymbol f:
			{
				if (f.Type is null) Owner?.EnsureFieldType(f);
				if (f.Type is null)
					return new ValueResult(ErrorExpr(Krn.CannotInferVariableType, $"тип '{f.Name}' ещё не выведен (циклическая зависимость инициализаторов?)", span));
				if (f.DeclaringType is not null && !f.IsStatic && HasThis)
					return new ValueResult(new BoundField(new BoundThis(SelfType!, span), f, false, f.Type, span));
				return new ValueResult(new BoundField(null, f, false, f.Type, span));
			}
			case OverloadSet os:
				return new GroupResult(name, os.Functions, null, false, null, null, span);
			case TypeDeclSymbol or NativeTypeBinding or TypeParamSymbol:
				return TypeFromSymbol(sym, name, typeArgs, span);
			case ModuleAliasSymbol alias:
				return new ModuleResult(alias.Path, alias.Module, alias.NativeModule, span);
			case PropertySymbol p:
				return new ValueResult(new BoundProperty(null, p, false, p.Type, span));
		}

		// встроенные типы по имени: i32, str, list...
		if (PrimitiveType.FromName(name) is not null || BuiltinGenericType.ArityOf(name) >= 0)
		{
			var t = Project.Resolver.Resolve(new Syn.NamedType(name, typeArgs, span), Scope, File.File);
			return new TypeResult(t, span, typeArgs.Count > 0);
		}

		// Ok / Err — конструкторы result
		if (name is "Ok" or "Err")
			return new GroupResult(name, [], null, false, null, null, span, IsConstructorGroup: true);

		// префикс пути модуля: `kern`, `web` в `web.server.run()`
		if (IsModulePrefix(name, out var module, out var native))
			return new ModuleResult(name, module, native, span);

		return new ValueResult(ErrorExpr(Krn.NameNotFound, $"имя '{name}' не найдено", span));
	}

	private bool IsModulePrefix(string path, out ModuleSymbol? module, out NativeModule? native)
	{
		module = Project.Modules.Find(path);
		native = Project.Binder.Source.FindModule(path);
		if (module is not null || native is not null) return true;
		string prefix = path + ".";
		return Project.Modules.All.Any(m => m.Name.StartsWith(prefix, StringComparison.Ordinal));
	}

	private BindResult TypeFromSymbol(Symbol sym, string name, IReadOnlyList<Syn.TypeRef> typeArgs, TextSpan span)
	{
		// generic-тип без аргументов: аргументы могут быть выведены по конструктору
		if (typeArgs.Count == 0)
		{
			if (sym is TypeDeclSymbol { TypeParams.Count: > 0 } d)
				return new TypeResult(d.ThisType, span, false);
			if (sym is NativeTypeBinding { Native.GenericParams.Count: > 0 } nb)
			{
				var ps = Project.Binder.TypeParamsOf(nb.Native);
				return new TypeResult(Project.Binder.TypeOf(nb.Native, [.. ps.Select(p => (TypeSymbol)p.AsType)]), span, false);
			}
		}
		var t = Project.Resolver.Resolve(new Syn.NamedType(name, typeArgs, span), Scope, File.File);
		return new TypeResult(t, span, typeArgs.Count > 0);
	}

	// ------------------------------------------------------------ доступ к членам

	private BindResult BindMember(Syn.MemberExpr m, IReadOnlyList<Syn.TypeRef> typeArgs)
	{
		var target = BindTarget(m.Target, []);
		switch (target)
		{
			case ValueResult v:
				return MemberOfValue(v.Value, m.Name, m.IsSafe, m.Span);

			case TypeResult t:
			{
				var stat = Members.FindStatic(t.Type, m.Name);
				if (!stat.IsEmpty) return FromStaticSet(stat, t.Type, m.Name, m.Span);
				// вложенный тип/квалифицированное имя
				return new ValueResult(ErrorExpr(Krn.MemberNotFound, $"у типа '{t.Type.ToDisplayString()}' нет статического члена '{m.Name}'", m.Span));
			}

			case ModuleResult mod:
			{
				string full = mod.Path + "." + m.Name;
				Symbol? sym = null;
				if (mod.Module?.Scope.LookupLocal(m.Name) is { } s && s.Access != Syn.Access.Priv) sym = s;
				else if (Project.Resolver.LookupInModule(mod.Path, m.Name) is { } found) sym = found;
				else if (mod.Native is { } nm)
				{
					// свободные функции/свойства/константы нативного модуля: `kern.math.pi`, `kern.math.sqrt(x)`
					var members = nm.Functions.Where(f => f.Name == m.Name).ToList();
					if (members.FirstOrDefault(f => f.Kind is Kern.Binding.Catalog.NativeMemberKind.Property or Kern.Binding.Catalog.NativeMemberKind.Field) is { } pm)
						sym = Project.Binder.Bind(pm);
					else if (members.Count > 0)
						sym = Project.Binder.BindGroup(m.Name, members);
				}
				if (sym is not null) return FromSymbol(sym, m.Name, typeArgs, m.Span, m);
				if (IsModulePrefix(full, out var sub, out var nat)) return new ModuleResult(full, sub, nat, m.Span);
				return new ValueResult(ErrorExpr(Krn.NameNotFound, $"в модуле '{mod.Path}' нет '{m.Name}'", m.Span));
			}

			case GroupResult g:
				return new ValueResult(ErrorExpr(Krn.NotAValue, $"у функции '{g.Name}' нет членов", m.Span));
			default:
				return target;
		}
	}

	public BindResult MemberOfValue(BoundExpr receiver, string name, bool isSafe, TextSpan span)
	{
		if (receiver.IsError) return new ValueResult(receiver);
		var type = receiver.Type;
		if (type is NullableType nt)
		{
			if (!isSafe)
				return new ValueResult(ErrorExpr(Krn.TypeMismatch,
					$"значение типа '{type.ToDisplayString()}' может быть nil: используйте `?.` или проверьте на nil", span));
			type = nt.Inner;
		}
		else if (isSafe)
			Warn(Krn.SafeCallOnNonNullable, $"оператор `?.` применён к значению типа '{type.ToDisplayString()}', которое не может быть nil", span);

		if (type is TupleType tup && tup.IndexOfName(name) is var ti and >= 0)
			return new ValueResult(new BoundTupleItem(receiver, ti, name, isSafe, WrapSafe(tup.Items[ti], isSafe), span));

		var ms = Members.FindInstance(type, name, File);
		if (ms.IsEmpty)
			return new ValueResult(ErrorExpr(Krn.MemberNotFound, $"у типа '{type.ToDisplayString()}' нет члена '{name}'", span));
		return FromMemberSet(ms, receiver, type, name, isSafe, span);
	}

	private BindResult FromMemberSet(MemberSet ms, BoundExpr receiver, TypeSymbol receiverType, string name, bool isSafe, TextSpan span)
	{
		if (ms.Functions.Count > 0 && ms.Field is null && ms.Property is null)
			return new GroupResult(name, ms.Functions, receiver, isSafe, receiverType, ms.OwnerMap, span);
		if (ms.Field is { } f)
		{
			CheckAccess(f, span);
			var ft = ms.FieldType ?? ErrorType.Instance;
			return new ValueResult(new BoundField(receiver, f, isSafe, WrapSafe(ft, isSafe), span));
		}
		if (ms.Property is { } p)
		{
			CheckAccess(p, span);
			return new ValueResult(new BoundProperty(receiver, p, isSafe, WrapSafe(ms.PropertyType ?? p.Type, isSafe), span));
		}
		return new GroupResult(name, ms.Functions, receiver, isSafe, receiverType, ms.OwnerMap, span);
	}

	private BindResult FromStaticSet(MemberSet ms, TypeSymbol owner, string name, TextSpan span)
	{
		if (ms.Field is { } f)
		{
			CheckAccess(f, span);
			var ft = f.IsEnumEntry ? owner : (ms.FieldType ?? ErrorType.Instance);
			return new ValueResult(new BoundField(null, f, false, ft, span));
		}
		if (ms.Property is { } p)
			return new ValueResult(new BoundProperty(null, p, false, ms.PropertyType ?? p.Type, span));
		if (ms.Functions.Count > 0)
			return new GroupResult(name, ms.Functions, null, false, owner, ms.OwnerMap, span);
		if (ms.NestedType is { } nested)
			return new TypeResult(nested.TypeParams.Count == 0 ? nested.ThisType : nested.ThisType, span, false);
		return new ValueResult(ErrorExpr(Krn.MemberNotFound, $"нет статического члена '{name}'", span));
	}

	private static TypeSymbol WrapSafe(TypeSymbol t, bool isSafe) =>
		isSafe && !IsVoid(t) && t is not NeverType ? NullableType.Of(t) : t;

	private void CheckAccess(Symbol member, TextSpan span)
	{
		if (member.Access != Syn.Access.Priv) return;
		var owner = member.DeclaringType;
		if (owner is null)
		{
			if (member.Container is ModuleSymbol m && !ReferenceEquals(m, File.Module))
				Error(Krn.InaccessibleMember, $"'{member.Name}' объявлен как priv в модуле '{m.Name}'", span);
			return;
		}
		for (var t = CurrentType; t is not null; t = t.Container as TypeDeclSymbol)
			if (ReferenceEquals(t, owner)) return;
		if (owner.File is not null && ReferenceEquals(owner.File, File.File)) return;
		Error(Krn.InaccessibleMember, $"член '{member.Name}' типа '{owner.Name}' объявлен как priv", span);
	}

	private BindResult BindParentMember(Syn.ParentMemberExpr pm)
	{
		if (!HasThis || SelfType is not DeclaredType self)
			return new ValueResult(ErrorExpr(Krn.ThisUnavailable, "`$[A]` допустим только внутри членов типа", pm.Span));
		var parent = self.Decl.Parents.FirstOrDefault(p => p.Decl?.Name == pm.Parent
			|| (p.Type is NativeType nt && nt.Symbol.Name == pm.Parent));
		if (parent is null)
			return new ValueResult(ErrorExpr(Krn.ParentMemberNotFound, $"'{pm.Parent}' не является родителем типа '{self.Decl.Name}'", pm.Span));
		NoteThisUse();
		var ptype = parent.Type.Substitute(self.TypeMap());
		var receiver = new BoundParentThis(ptype, pm.Span);
		var ms = Members.FindInstance(ptype, pm.Member, File, includeExtensions: false);
		if (ms.IsEmpty)
			return new ValueResult(ErrorExpr(Krn.ParentMemberNotFound, $"у родителя '{pm.Parent}' нет члена '{pm.Member}'", pm.Span));
		return FromMemberSet(ms, receiver, ptype, pm.Member, false, pm.Span);
	}
}
