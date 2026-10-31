using Kern.Compiler.Semantics.Project;
using Kern.Compiler.Semantics.Scopes;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Declare.Collect;

/// <summary>
/// Фаза 4: члены и сигнатуры. Для каждой перегрузки создаётся отдельный <see cref="FunctionSymbol"/>;
/// свойства порождают отдельные функции-аксессоры (get_/set_/init_) и, для автосвойств, скрытое backing-поле.
/// Тела не анализируются (часть 2).
/// </summary>
public sealed class MemberDeclarer(CollectContext ctx)
{
	// ============================================================ верхний уровень

	public void DeclareTopLevel(FileModel file)
	{
		var module = file.Module;
		foreach (var decl in file.Unit.Decls)
		{
			switch (decl)
			{
				case Syn.FunDecl f:
				{
					var fn = BuildFunction(file, file.Scope, module, f.Name, FunctionKind.Function, f, f.NameSpan,
						f.TypeParams, f.Params, f.ReturnType, f.Body, receiver: null, f.HasParams);
					if (f.ParentSelector is not null)
						ctx.Error(Krn.UnknownParentSelector, "выбор родителя `$[A]` допустим только внутри типа", file.File, f.NameSpan);
					Register(fn, module.Scope, file, f.NameSpan);
					module.Functions.Add(fn);
					break;
				}
				case Syn.ExtensionFunDecl ef:
				{
					var fn = BuildFunction(file, file.Scope, module, ef.Name, FunctionKind.Method, ef, ef.NameSpan,
						ef.TypeParams, ef.Params, ef.ReturnType, ef.Body, ef.Receiver, hasParams: ef.Params.Count > 0);
					Register(fn, module.Scope, file, ef.NameSpan);
					module.Functions.Add(fn);
					break;
				}
				case Syn.ExtensionOpDecl eo:
				{
					var fn = BuildFunction(file, file.Scope, module, TopLevelDeclarer.OpName(eo.Symbol), FunctionKind.Operator, eo, eo.Span,
						[], eo.Params, eo.ReturnType, eo.Body, eo.Receiver, hasParams: true);
					fn.OpSymbol = eo.Symbol;
					Register(fn, module.Scope, file, eo.Span);
					module.Functions.Add(fn);
					break;
				}
				case Syn.FieldDecl fd:
					if (module.Scope.LookupLocal(fd.Name) is FieldSymbol fs && ReferenceEquals(fs.Syntax, fd))
						fs.Type = fd.Type is null ? null : ctx.Resolver.Resolve(fd.Type, file.Scope, file.File);
					break;
				case Syn.ExtensionPropDecl ep:
					DeclareExtensionProperty(file, ep);
					break;
				case Syn.PropertyDecl pd:
					ctx.Error(Krn.InvalidMemberHere, "свойство нельзя объявить на верхнем уровне; используйте поле или расширение свойства (`T Type.имя выражение`)", file.File, pd.NameSpan);
					break;
				case Syn.OpDecl od:
					ctx.Error(Krn.InvalidMemberHere, "оператор на верхнем уровне должен указывать тип-получатель: `op Type.+(...)`", file.File, od.Span);
					break;
			}
		}
	}

	// ============================================================ члены типов

	public void DeclareTypeMembers(TypeDeclSymbol type)
	{
		if (type.Syntax is Syn.UnionAliasDecl || type.File is null) return;
		var file = ctx.FileOf(type.File);
		var scope = type.MemberScope!;

		IReadOnlyList<Syn.Param>? primary;
		IReadOnlyList<Syn.Decl> members;
		switch (type.Syntax)
		{
			case Syn.TypeDecl t: primary = t.PrimaryCtor; members = t.Members; break;
			case Syn.EnumDecl e: primary = e.PrimaryCtor; members = e.Members; break;
			default: return;
		}

		if (primary is not null) DeclarePrimaryConstructor(file, type, scope, primary);
		if (type.Syntax is Syn.EnumDecl ed) DeclareEnumEntries(file, type, scope, ed);

		foreach (var member in members)
		{
			switch (member)
			{
				case Syn.FunDecl f: DeclareMethod(file, type, scope, f); break;
				case Syn.OpDecl od: DeclareOperator(file, type, scope, od); break;
				case Syn.CtorDecl cd: DeclareConstructor(file, type, scope, cd); break;
				case Syn.FieldDecl fd: DeclareField(file, type, scope, fd); break;
				case Syn.PropertyDecl pd: DeclareProperty(file, type, scope, pd); break;
				case Syn.ExtensionFunDecl or Syn.ExtensionOpDecl or Syn.ExtensionPropDecl:
					ctx.Error(Krn.InvalidMemberHere, "расширения объявляются на верхнем уровне модуля, а не внутри типа", file.File, member.Span);
					break;
				// вложенные типы созданы на первой фазе
			}
		}
	}

	private void DeclareMethod(FileModel file, TypeDeclSymbol type, Scope scope, Syn.FunDecl f)
	{
		var fn = BuildFunction(file, scope, type, f.Name, FunctionKind.Method, f, f.NameSpan,
			f.TypeParams, f.Params, f.ReturnType, f.Body, receiver: null, f.HasParams);
		if (f.ParentSelector is not null)
		{
			fn.ParentSelector = f.ParentSelector;
			fn.ParentSelectorType = ResolveSelector(type, f.ParentSelector, file, f.NameSpan);
		}
		if (Register(fn, scope, file, f.NameSpan)) type.Functions.Add(fn);
	}

	private void DeclareOperator(FileModel file, TypeDeclSymbol type, Scope scope, Syn.OpDecl od)
	{
		var fn = BuildFunction(file, scope, type, TopLevelDeclarer.OpName(od.Symbol), FunctionKind.Operator, od, od.Span,
			[], od.Params, od.ReturnType, od.Body, receiver: null, hasParams: true);
		fn.OpSymbol = od.Symbol;
		if (Register(fn, scope, file, od.Span)) type.Functions.Add(fn);
	}

	private void DeclareConstructor(FileModel file, TypeDeclSymbol type, Scope scope, Syn.CtorDecl cd)
	{
		var fn = BuildFunction(file, scope, type, "ctor", FunctionKind.Constructor, cd, cd.Span,
			[], cd.Params, null, cd.Body, receiver: null, hasParams: true);
		if (!HasErrorTypes(fn))
		{
			var dup = type.Constructors.FirstOrDefault(c => c.HasSameSignature(fn));
			if (dup is not null)
			{
				ctx.Error(Krn.DuplicateSignature, $"конструктор с такой сигнатурой уже объявлен: {fn.ToDisplayString()}", file.File, cd.Span);
				return;
			}
		}
		type.Constructors.Add(fn);
	}

	private void DeclarePrimaryConstructor(FileModel file, TypeDeclSymbol type, Scope scope, IReadOnlyList<Syn.Param> primary)
	{
		var fn = BuildFunction(file, scope, type, "ctor", FunctionKind.Constructor, type.Syntax!, type.Span,
			[], primary, null, null, receiver: null, hasParams: true);
		fn.IsPrimaryCtor = true;
		type.PrimaryConstructor = fn;
		type.Constructors.Add(fn);

		// Параметры первичного конструктора порождают поля (см. kern.md §8).
		foreach (var p in fn.Parameters)
		{
			var syn = primary.First(s => s.Name == p.Name);
			var field = new FieldSymbol(p.Name, p.EffectiveType)
			{
				Container = type,
				File = file.File,
				Span = syn.NameSpan,
				Access = syn.Mods.Access,
				FromParameter = p,
			};
			p.Field = field;
			if (scope.Declare(field) is { } existing)
			{
				ctx.Error(Krn.MemberNameConflict, $"поле '{p.Name}' из параметра конструктора конфликтует с другим членом ({existing.Kind})", file.File, syn.NameSpan);
				continue;
			}
			type.Fields.Add(field);
		}
	}

	private void DeclareEnumEntries(FileModel file, TypeDeclSymbol type, Scope scope, Syn.EnumDecl ed)
	{
		foreach (var e in ed.Entries)
		{
			var field = new FieldSymbol(e.Name, type.ThisType)
			{
				Container = type,
				File = file.File,
				Span = e.NameSpan,
				Access = Syn.Access.Pub,
				IsStatic = true,
				IsEnumEntry = true,
				EnumSyntax = e,
				EnumArgs = e.Args,
				Init = e.Value,
			};
			if (scope.Declare(field) is { } existing)
			{
				bool dupEntry = existing is FieldSymbol { IsEnumEntry: true };
				ctx.Error(dupEntry ? Krn.DuplicateEnumEntry : Krn.MemberNameConflict,
					dupEntry ? $"элемент enum '{e.Name}' объявлен повторно" : $"имя '{e.Name}' уже занято другим членом типа '{type.Name}'",
					file.File, e.NameSpan);
				continue;
			}
			type.Fields.Add(field);
			type.EnumEntries.Add(field);
		}
	}

	private void DeclareField(FileModel file, TypeDeclSymbol type, Scope scope, Syn.FieldDecl fd)
	{
		var field = new FieldSymbol(fd.Name, fd.Type is null ? null : ctx.Resolver.Resolve(fd.Type, scope, file.File))
		{
			Container = type,
			File = file.File,
			Span = fd.NameSpan,
			Access = fd.Mods.Access,
			IsStatic = fd.Mods.IsStatic,
			IsRequired = fd.Mods.IsRequired,
			Init = fd.Init,
			Syntax = fd,
		};
		if (fd.ParentSelector is not null)
		{
			field.ParentSelector = fd.ParentSelector;
			field.ParentSelectorType = ResolveSelector(type, fd.ParentSelector, file, fd.NameSpan);
		}
		if (scope.Declare(field) is { } existing)
		{
			ctx.Error(Krn.MemberNameConflict, $"имя '{fd.Name}' уже занято другим членом типа '{type.Name}' ({existing.Kind})", file.File, fd.NameSpan);
			return;
		}
		type.Fields.Add(field);
		ctx.QueueAttributes(field, fd.Attributes, scope, file.File);
	}

	// ============================================================ свойства

	private void DeclareProperty(FileModel file, TypeDeclSymbol type, Scope scope, Syn.PropertyDecl pd)
	{
		var prop = new PropertySymbol(pd.Name, ctx.Resolver.Resolve(pd.Type, scope, file.File))
		{
			Container = type,
			File = file.File,
			Span = pd.NameSpan,
			Access = pd.Mods.Access,
			IsStatic = pd.Mods.IsStatic,
			Init = pd.Init,
			Syntax = pd,
		};
		if (pd.ParentSelector is not null)
		{
			prop.ParentSelector = pd.ParentSelector;
			prop.ParentSelectorType = ResolveSelector(type, pd.ParentSelector, file, pd.NameSpan);
		}

		bool isAbstract = pd.Mods.IsAbstract;
		bool anyAuto = false;

		if (pd.Accessors.Count == 0)
			ctx.Error(Krn.InvalidMemberHere, $"у свойства '{pd.Name}' нет аксессоров", file.File, pd.NameSpan);
		if (isAbstract && !type.IsAbstract)
			ctx.Error(Krn.AbstractInConcreteType, $"абстрактное свойство '{pd.Name}' в неабстрактном типе '{type.Name}'", file.File, pd.NameSpan);

		foreach (var acc in pd.Accessors)
		{
			var kind = acc.Kind switch
			{
				Syn.AccessorKind.Get => FunctionKind.Getter,
				Syn.AccessorKind.Set => FunctionKind.Setter,
				_ => FunctionKind.Init,
			};
			var slot = kind switch { FunctionKind.Getter => prop.Getter, FunctionKind.Setter => prop.Setter, _ => prop.Initer };
			if (slot is not null)
			{
				ctx.Error(Krn.DuplicateAccessor, $"аксессор {AccessorWord(kind)} свойства '{pd.Name}' объявлен повторно", file.File, acc.Span);
				continue;
			}
			if (isAbstract && acc.Body is not null)
				ctx.Error(Krn.AbstractWithBody, $"абстрактный аксессор свойства '{pd.Name}' не может иметь тело", file.File, acc.Span);

			var fn = new FunctionSymbol(AccessorPrefix(kind) + pd.Name, kind)
			{
				Container = type,
				File = file.File,
				Span = acc.Span,
				Access = acc.Access != Syn.Access.Default ? acc.Access : pd.Mods.Access,
				IsStatic = prop.IsStatic,
				IsOpen = pd.Mods.IsOpen,
				IsOverride = pd.Mods.IsOverride,
				IsAbstract = isAbstract,
				Property = prop,
				Syntax = acc,
				Body = acc.Body,
				HasParams = kind != FunctionKind.Getter,
				ReturnType = kind == FunctionKind.Getter ? prop.Type : PrimitiveType.Void,
			};
			if (kind != FunctionKind.Getter)
				fn.Parameters.Add(new ParameterSymbol(acc.ParamName ?? "value", prop.Type, 0) { Container = fn, File = file.File, Span = acc.Span });

			switch (kind)
			{
				case FunctionKind.Getter: prop.Getter = fn; break;
				case FunctionKind.Setter: prop.Setter = fn; break;
				default: prop.Initer = fn; break;
			}
			if (acc.Body is null && !isAbstract) anyAuto = true;
		}

		if (prop.Setter is not null && prop.Initer is not null)
			ctx.Error(Krn.DuplicateAccessor, $"свойство '{pd.Name}' не может одновременно иметь `set` и `init`", file.File, pd.NameSpan);

		if (anyAuto)
		{
			prop.IsAuto = true;
			var backing = new FieldSymbol($"<{pd.Name}>k", prop.Type)
			{
				Container = type,
				File = file.File,
				Span = pd.NameSpan,
				Access = Syn.Access.Priv,
				IsStatic = prop.IsStatic,
				BackingFor = prop,
			};
			prop.BackingField = backing;
			type.Fields.Add(backing);
		}

		if (scope.Declare(prop) is { } existing)
		{
			ctx.Error(Krn.MemberNameConflict, $"имя '{pd.Name}' уже занято другим членом типа '{type.Name}' ({existing.Kind})", file.File, pd.NameSpan);
			if (prop.BackingField is { } bf) type.Fields.Remove(bf);
			return;
		}
		type.Properties.Add(prop);
		ctx.QueueAttributes(prop, pd.Attributes, scope, file.File);
	}

	private void DeclareExtensionProperty(FileModel file, Syn.ExtensionPropDecl ep)
	{
		var module = file.Module;
		var type = ctx.Resolver.Resolve(ep.Type, file.Scope, file.File);
		var receiver = ctx.Resolver.Resolve(ep.Receiver, file.Scope, file.File);
		CheckReceiver(receiver, file, ep.Receiver.Span);

		var prop = new PropertySymbol(ep.Name, type)
		{
			Container = module,
			File = file.File,
			Span = ep.NameSpan,
			Access = ep.Mods.Access,
			ExtensionOf = receiver,
			Syntax = null,
		};
		var getter = new FunctionSymbol("get_" + ep.Name, FunctionKind.Getter)
		{
			Container = module,
			File = file.File,
			Span = ep.NameSpan,
			Access = ep.Mods.Access,
			ExtensionOf = receiver,
			Property = prop,
			Syntax = ep,
			Body = ep.Body,
			HasParams = false,
			ReturnType = type,
		};
		prop.Getter = getter;

		if (!type.IsError && !receiver.IsError
			&& module.ExtensionProperties.Any(o => o.Name == ep.Name && o.ExtensionOf is { } r && r.Equals(receiver)))
		{
			ctx.Error(Krn.DuplicateSignature, $"расширение свойства '{ep.Name}' для '{receiver.ToDisplayString()}' уже объявлено", file.File, ep.NameSpan);
			return;
		}
		module.ExtensionProperties.Add(prop);
		ctx.QueueAttributes(prop, ep.Attributes, file.Scope, file.File);
	}

	// ============================================================ функции

	/// <summary>
	/// Создаёт символ функции с generic-параметрами, получателем расширения, параметрами и возвращаемым типом.
	/// Регистрацию в области выполняет <see cref="Register"/>.
	/// </summary>
	private FunctionSymbol BuildFunction(
		FileModel file, Scope parentScope, Symbol container, string name, FunctionKind kind, Syn.Decl syntax, TextSpan nameSpan,
		IReadOnlyList<Syn.TypeParam> typeParams, IReadOnlyList<Syn.Param> ps, Syn.TypeRef? ret, Syn.Expr? body,
		Syn.TypeRef? receiver, bool hasParams)
	{
		var mods = syntax.Mods;
		var fn = new FunctionSymbol(name, kind)
		{
			Container = container,
			File = file.File,
			Span = nameSpan,
			Access = mods.Access,
			IsAsync = mods.IsAsync,
			IsStatic = mods.IsStatic,
			IsOpen = mods.IsOpen,
			IsOverride = mods.IsOverride,
			IsAbstract = mods.IsAbstract,
			HasParams = hasParams,
			Syntax = syntax,
			Body = body,
		};

		var fnScope = ctx.Resolver.DeclareTypeParams(typeParams, fn, fn.TypeParams, parentScope, file.File);
		ctx.Resolver.ResolveConstraints(typeParams, fn.TypeParams, fnScope, file.File);

		if (receiver is not null)
		{
			fn.ExtensionOf = ctx.Resolver.Resolve(receiver, fnScope, file.File);
			CheckReceiver(fn.ExtensionOf, file, receiver.Span);
		}

		var seen = new HashSet<string>();
		for (int i = 0; i < ps.Count; i++)
		{
			var p = ps[i];
			if (!seen.Add(p.Name))
			{
				ctx.Error(Krn.DuplicateParameter, $"параметр '{p.Name}' объявлен повторно", file.File, p.NameSpan);
				continue;
			}
			if (p.IsVariadic && i != ps.Count - 1)
				ctx.Error(Krn.VariadicNotLast, $"variadic-параметр '{p.Name}' должен быть последним", file.File, p.NameSpan);

			var param = new ParameterSymbol(p.Name, ctx.Resolver.Resolve(p.Type, fnScope, file.File), fn.Parameters.Count)
			{
				Container = fn,
				File = file.File,
				Span = p.NameSpan,
				Default = p.Default,
				IsVariadic = p.IsVariadic,
				AutoInit = p.AutoInit,
				Mods = p.Mods,
			};
			fn.Parameters.Add(param);
			ctx.QueueAttributes(param, p.Attributes, fnScope, file.File);
		}

		// Нет `-> T`: функции и методы возвращают void (вывод типа остаётся только у лямбд и операторов)
		fn.ReturnType = ret is not null ? ctx.Resolver.Resolve(ret, fnScope, file.File)
			: fn.FunctionKind is FunctionKind.Function or FunctionKind.Method ? PrimitiveType.Void : null;
		ctx.QueueAttributes(fn, syntax.Attributes, fnScope, file.File);
		ValidateBody(fn, container as TypeDeclSymbol, file, nameSpan);
		return fn;
	}

	private void ValidateBody(FunctionSymbol fn, TypeDeclSymbol? owner, FileModel file, TextSpan span)
	{
		if (fn.FunctionKind is not (FunctionKind.Function or FunctionKind.Method or FunctionKind.Operator)) return;

		if (fn.IsAbstract)
		{
			if (fn.Body is not null)
				ctx.Error(Krn.AbstractWithBody, $"абстрактный метод '{fn.Name}' не может иметь тело", file.File, span);
			if (owner is null || !owner.IsAbstract)
				ctx.Error(Krn.AbstractInConcreteType, $"абстрактный метод '{fn.Name}' допустим только в `@type`", file.File, span);
		}
		else if (fn.Body is null && fn.ParentSelector is null && (fn.Syntax is not Syn.FunDecl { ParentSelector: not null }))
		{
			ctx.Error(Krn.MissingBody, $"у метода '{fn.Name}' нет тела", file.File, span);
		}
	}

	private void CheckReceiver(TypeSymbol receiver, FileModel file, TextSpan span)
	{
		if (receiver.IsError) return;
		if (receiver == PrimitiveType.Void || receiver is NilType || receiver is NoneType)
			ctx.Error(Krn.InvalidExtensionReceiver, $"тип '{receiver.ToDisplayString()}' нельзя расширять", file.File, span);
	}

	private static bool HasErrorTypes(FunctionSymbol fn) =>
		fn.Parameters.Any(p => p.Type.IsError) || (fn.ExtensionOf?.IsError ?? false);

	/// <summary>Добавляет перегрузку в область. false — отклонена (дубликат сигнатуры или конфликт имени).</summary>
	private bool Register(FunctionSymbol fn, Scope scope, FileModel file, TextSpan span)
	{
		if (HasErrorTypes(fn))
		{
			// Типы не разрешились (ошибка уже выдана) — не придумываем ложных дубликатов.
			scope.GetOrCreateGroup(fn.Name, fn.Container, out _)?.AddUnchecked(fn);
			return true;
		}
		if (scope.DeclareFunction(fn, out var duplicate, out var conflict)) return true;

		if (duplicate is not null)
			ctx.Error(Krn.DuplicateSignature, $"перегрузка с такой сигнатурой уже объявлена: {fn.ToDisplayString()}", file.File, span);
		else if (conflict is not null)
			ctx.Error(Krn.MemberNameConflict, $"имя '{fn.Name}' уже занято ({conflict.Kind}) и не может быть именем функции", file.File, span);
		return false;
	}

	// ============================================================ выбор родителя

	private TypeSymbol? ResolveSelector(TypeDeclSymbol owner, string selector, FileModel file, TextSpan span)
	{
		var match = owner.Parents.FirstOrDefault(p => ParentName(p.Type) == selector);
		if (match is null)
		{
			ctx.Error(Krn.UnknownParentSelector, $"'{selector}' не является родителем типа '{owner.Name}'", file.File, span);
			return null;
		}
		return match.Type;
	}

	private static string ParentName(TypeSymbol t) => t switch
	{
		DeclaredType d => d.Decl.Name,
		NativeType n => n.Symbol.Name,
		PrimitiveType p => p.Name,
		_ => t.ToDisplayString(),
	};

	private static string AccessorPrefix(FunctionKind kind) => kind switch
	{
		FunctionKind.Getter => "get_",
		FunctionKind.Setter => "set_",
		_ => "init_",
	};

	private static string AccessorWord(FunctionKind kind) => kind switch
	{
		FunctionKind.Getter => "`get`",
		FunctionKind.Setter => "`set`",
		_ => "`init`",
	};
}
