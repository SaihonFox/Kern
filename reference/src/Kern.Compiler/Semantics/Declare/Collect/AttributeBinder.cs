using Kern.Compiler.Semantics.Symbols;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Declare.Collect;

/// <summary>Фаза 5: разрешает `#[Имя(args)]` в объявленные `attribute`-типы. Аргументы не вычисляются.</summary>
public sealed class AttributeBinder(CollectContext ctx)
{
	/// <summary>Атрибуты, известные компилятору без объявления.</summary>
	public static readonly IReadOnlySet<string> BuiltIn = new HashSet<string> { "native", "test", "entry", "interface", "Inline", "Deprecated", "get", "post", "put", "patch", "delete" };

	public void BindAll()
	{
		foreach (var pending in ctx.PendingAttributes)
		{
			var applied = new List<AttributeApplication>();
			foreach (var use in pending.Uses)
				applied.Add(new AttributeApplication(use, Resolve(pending, use)));
			pending.Target.Attributes = applied;
		}
		ctx.PendingAttributes.Clear();
	}

	private TypeDeclSymbol? Resolve(PendingAttributes p, Syn.AttributeUse use)
	{
		Symbol? sym = use.Name.Contains('.')
			? ctx.Resolver.ResolveQualified(use.Name.Split('.'), p.Scope)
			: p.Scope.LookupType(use.Name);

		switch (sym)
		{
			case TypeDeclSymbol { DeclKind: TypeDeclKind.Attribute } d:
				return d;
			case TypeDeclSymbol or NativeTypeBinding:
				if (sym is TypeDeclSymbol)
					ctx.Error(Krn.NotAnAttribute, $"'{use.Name}' не является атрибутом (`attribute`)", p.File, use.Span);
				return null;
			default:
				if (!BuiltIn.Contains(use.Name))
					ctx.Warning(Krn.UnknownAttribute, $"атрибут '{use.Name}' не найден", p.File, use.Span);
				return null;
		}
	}
}
