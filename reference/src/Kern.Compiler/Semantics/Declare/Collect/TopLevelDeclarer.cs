using Kern.Compiler.Semantics.Project;
using Kern.Compiler.Semantics.Scopes;
using Kern.Compiler.Semantics.Symbols;
using Kern.Core;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Declare.Collect;

/// <summary>
/// Фаза 1: «скелеты». Создаёт символы типов (включая вложенные) вместе с generic-параметрами и областями членов,
/// пустые группы перегрузок для функций верхнего уровня (чтобы импорты могли ссылаться на них «вживую»)
/// и глобальные поля. Сигнатуры и члены — в <see cref="MemberDeclarer"/>.
/// </summary>
public sealed class TopLevelDeclarer(CollectContext ctx)
{
	public void Declare(FileModel file)
	{
		foreach (var decl in file.Unit.Decls)
			DeclareTopLevel(file, decl);
	}

	private void DeclareTopLevel(FileModel file, Syn.Decl decl)
	{
		var module = file.Module;
		switch (decl)
		{
			case Syn.TypeDecl or Syn.EnumDecl or Syn.UnionAliasDecl:
				DeclareType(file, decl, module, module.Scope, file.Scope);
				break;

			case Syn.FunDecl f:
				module.Scope.GetOrCreateGroup(f.Name, module, out _);
				break;
			case Syn.ExtensionFunDecl ef:
				module.Scope.GetOrCreateGroup(ef.Name, module, out _);
				break;
			case Syn.ExtensionOpDecl eo:
				module.Scope.GetOrCreateGroup(OpName(eo.Symbol), module, out _);
				break;

			case Syn.FieldDecl fd:
				DeclareGlobalField(file, fd);
				break;
		}
	}

	public static string OpName(string symbol) => "op" + symbol;

	private void DeclareGlobalField(FileModel file, Syn.FieldDecl fd)
	{
		var module = file.Module;
		var field = new FieldSymbol(fd.Name, null)
		{
			Container = module,
			File = file.File,
			Span = fd.NameSpan,
			Access = fd.Mods.Access,
			IsStatic = true,
			IsRequired = fd.Mods.IsRequired,
			Init = fd.Init,
			Syntax = fd,
		};
		if (module.Scope.Declare(field) is { } existing)
		{
			ctx.Error(Krn.MemberNameConflict, $"имя '{fd.Name}' уже объявлено в модуле '{module.Name}' ({Describe(existing)})", file.File, fd.NameSpan);
			return;
		}
		module.Fields.Add(field);
		ctx.QueueAttributes(field, fd.Attributes, file.Scope, file.File);
	}

	/// <summary>Создаёт символ типа и рекурсивно — вложенных. null — имя занято (ошибка уже выдана).</summary>
	private TypeDeclSymbol? DeclareType(FileModel file, Syn.Decl decl, Symbol container, Scope declareIn, Scope parentScope)
	{
		var (name, nameSpan, kind, typeParams) = decl switch
		{
			Syn.TypeDecl t => (t.Name, t.NameSpan, MapKind(t.Kind), t.TypeParams),
			Syn.EnumDecl e => (e.Name, e.NameSpan, TypeDeclKind.Enum, (IReadOnlyList<Syn.TypeParam>)[]),
			Syn.UnionAliasDecl u => (u.Name, u.NameSpan, TypeDeclKind.Union, u.TypeParams),
			_ => throw new InvalidOperationException("не объявление типа"),
		};

		var sym = new TypeDeclSymbol(name, kind)
		{
			Container = container,
			File = file.File,
			Span = nameSpan,
			Access = decl.Mods.Access,
			Syntax = decl,
			IsOpen = decl.Mods.IsOpen,
		};
		if (declareIn.Declare(sym) is { } existing)
		{
			ctx.Error(Krn.DuplicateType, $"имя '{name}' уже объявлено ({Describe(existing)})", file.File, nameSpan);
			return null;
		}

		var tpScope = ctx.Resolver.DeclareTypeParams(typeParams, sym, sym.TypeParams, parentScope, file.File);
		sym.MemberScope = new Scope(tpScope, $"type {name}");

		ctx.Types.Add(sym);
		if (container is ModuleSymbol m) m.Types.Add(sym);
		else if (container is TypeDeclSymbol outer) outer.NestedTypes.Add(sym);
		ctx.QueueAttributes(sym, decl.Attributes, parentScope, file.File);

		// Вложенные типы.
		IReadOnlyList<Syn.Decl> members = decl switch
		{
			Syn.TypeDecl t => t.Members,
			Syn.EnumDecl e => e.Members,
			_ => [],
		};
		foreach (var member in members)
			if (member is Syn.TypeDecl or Syn.EnumDecl or Syn.UnionAliasDecl)
				DeclareType(file, member, sym, sym.MemberScope, sym.MemberScope);

		return sym;
	}

	private static TypeDeclKind MapKind(Syn.TypeKind kind) => kind switch
	{
		Syn.TypeKind.AbstractType => TypeDeclKind.AbstractClass,
		Syn.TypeKind.Unit => TypeDeclKind.Unit,
		Syn.TypeKind.ImmutableUnit => TypeDeclKind.ImmutableUnit,
		Syn.TypeKind.Attribute => TypeDeclKind.Attribute,
		_ => TypeDeclKind.Class,
	};

	private static string Describe(Symbol s) => s.Kind switch
	{
		SymbolKind.Type => "тип",
		SymbolKind.OverloadSet => "функция",
		SymbolKind.Field => "поле",
		SymbolKind.Property => "свойство",
		_ => "символ",
	};
}
