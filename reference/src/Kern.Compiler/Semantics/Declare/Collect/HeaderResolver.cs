using Kern.Binding.Catalog;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Declare.Collect;

/// <summary>
/// Фаза 3: заголовки типов — ограничения generic-параметров, список родителей, члены union-алиасов.
/// Выполняется после разрешения импортов и до объявления членов (которым нужны родители для `$[A]`).
/// </summary>
public sealed class HeaderResolver(CollectContext ctx)
{
	public void ResolveAll(IReadOnlyList<TypeDeclSymbol> types)
	{
		foreach (var t in types) ResolveHeader(t);
		foreach (var t in types) BreakCycles(t);
	}

	private void ResolveHeader(TypeDeclSymbol type)
	{
		var scope = type.MemberScope!;
		var file = type.File!;

		switch (type.Syntax)
		{
			case Syn.TypeDecl td:
				ctx.Resolver.ResolveConstraints(td.TypeParams, type.TypeParams, scope, file);
				ResolveParents(type, td.Parents);
				break;

			case Syn.EnumDecl ed:
				ResolveParents(type, ed.Parents);
				break;

			case Syn.UnionAliasDecl ua:
				ctx.Resolver.ResolveConstraints(ua.TypeParams, type.TypeParams, scope, file);
				foreach (var m in ua.Union.Members)
				{
					var resolved = ctx.Resolver.Resolve(m, scope, file);
					if (!resolved.IsError) type.UnionMembers.Add(resolved);
				}
				break;
		}
	}

	private void ResolveParents(TypeDeclSymbol type, IReadOnlyList<Syn.ParentRef> parents)
	{
		var scope = type.MemberScope!;
		var file = type.File!;

		if (type.IsValueType && parents.Count > 0)
		{
			ctx.Error(Krn.InvalidParent, "`unit` не поддерживает наследование", file, parents[0].Span);
			return;
		}

		foreach (var p in parents)
		{
			var resolved = ctx.Resolver.Resolve(p.Type, scope, file);
			if (resolved.IsError) continue;

			if (!IsValidParent(type, resolved, p, out var message))
			{
				ctx.Error(message.code, message.text, file, p.Span);
				continue;
			}
			if (type.Parents.Any(e => e.Type.Equals(resolved)))
			{
				ctx.Error(Krn.DuplicateParent, $"родитель '{resolved.ToDisplayString()}' указан повторно", file, p.Span);
				continue;
			}
			type.Parents.Add(new ParentInfo(resolved, p.Args, p));
		}
	}

	private static bool IsValidParent(TypeDeclSymbol type, TypeSymbol parent, Syn.ParentRef syntax, out (string code, string text) error)
	{
		error = default;
		switch (parent)
		{
			case DeclaredType dt:
				var d = dt.Decl;
				if (ReferenceEquals(d, type))
				{
					error = (Krn.InheritanceCycle, $"тип '{type.Name}' не может наследоваться от самого себя");
					return false;
				}
				if (d.DeclKind is TypeDeclKind.Enum or TypeDeclKind.Union or TypeDeclKind.Unit or TypeDeclKind.ImmutableUnit)
				{
					error = (Krn.InvalidParent, $"'{d.Name}' не может быть родителем (это {KindName(d.DeclKind)})");
					return false;
				}
				if (d.DeclKind == TypeDeclKind.Attribute && type.DeclKind != TypeDeclKind.Attribute)
				{
					error = (Krn.InvalidParent, $"атрибут '{d.Name}' не может быть родителем обычного типа");
					return false;
				}
				if (!d.IsOpen && !d.IsAbstract)
				{
					error = (Krn.ParentNotOpen, $"тип '{d.Name}' не объявлен `open`, наследоваться от него нельзя");
					return false;
				}
				return true;

			case NativeType nt:
				if (nt.Symbol.IsOpen || nt.Symbol.Category == NativeTypeCategory.Interface) return true;
				error = (nt.Symbol.Category is NativeTypeCategory.Class
					? (Krn.ParentNotOpen, $"нативный тип '{nt.Symbol.Name}' не открыт для наследования")
					: (Krn.InvalidParent, $"'{nt.Symbol.Name}' не может быть родителем"));
				return false;

			default:
				error = (Krn.InvalidParent, $"'{parent.ToDisplayString()}' не может быть родителем");
				return false;
		}
	}

	private static string KindName(TypeDeclKind kind) => kind switch
	{
		TypeDeclKind.Enum => "enum",
		TypeDeclKind.Union => "union-алиас",
		TypeDeclKind.Unit or TypeDeclKind.ImmutableUnit => "unit",
		TypeDeclKind.Attribute => "атрибут",
		_ => "тип",
	};

	// ------------------------------------------------------------ циклы наследования

	private enum Mark { None, Active, Done }
	private readonly Dictionary<TypeDeclSymbol, Mark> _marks = [];

	private void BreakCycles(TypeDeclSymbol type)
	{
		if (_marks.GetValueOrDefault(type) != Mark.None) return;
		_marks[type] = Mark.Active;
		foreach (var parent in type.Parents.ToList())
		{
			if (parent.Decl is not { } pd) continue;
			var state = _marks.GetValueOrDefault(pd);
			if (state == Mark.Active)
			{
				ctx.Error(Krn.InheritanceCycle, $"циклическое наследование: '{type.Name}' → '{pd.Name}'", type.File!, parent.Syntax.Span);
				type.Parents.Remove(parent);
			}
			else if (state == Mark.None) BreakCycles(pd);
		}
		_marks[type] = Mark.Done;
	}
}
