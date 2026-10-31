using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding.Core;

/// <summary>
/// Рефлексия на этапе компиляции: вызовы вида `field_names&lt;User&gt;()` вычисляются биндером и заменяются константой (строкой, bool или
/// списком строк) — в байткоде остаётся готовый литерал, во время выполнения рефлексия не нужна. Применимо к типам, объявленным в коде
/// (type/unit/@type). Для типов из C# и встроенных используйте `typeof&lt;T&gt;()` (рантайм-рефлексия).
/// </summary>
public sealed partial class Binder
{
	private static readonly HashSet<string> MetaIntrinsics =
	[
		"name_of", "field_names", "property_names", "method_names", "parent_names", "attribute_names", "has_attribute",
	];

	private bool TryBindMeta(Syn.CallExpr c, out BoundExpr result)
	{
		result = null!;
		if (c.Callee is not Syn.IdentExpr { Name: var name } || !MetaIntrinsics.Contains(name) || c.TypeArgs.Count != 1) return false;

		int want = name == "has_attribute" ? 1 : 0;
		if (c.Args.Count != want)
		{
			result = ErrorExpr(Krn.TypeMismatch, name == "has_attribute"
				? "has_attribute<T>(\"имя\") принимает один аргумент — имя атрибута"
				: $"{name}<T>() не принимает аргументов", c.Span);
			return true;
		}

		var type = ResolveType(c.TypeArgs[0]);
		if (type.IsError) { result = new BoundErrorExpr(c.Span); return true; }
		if (type is not DeclaredType { Decl: var decl })
		{
			result = ErrorExpr(Krn.TypeMismatch, $"{name}<T>: тип '{type.ToDisplayString()}' объявлен не в коде Kern — используйте typeof<T>() (рантайм-рефлексия)", c.Span);
			return true;
		}

		switch (name)
		{
			case "name_of":
				result = new BoundStringLiteral(decl.Name, c.Span);
				return true;
			case "has_attribute":
			{
				var arg = Bind(c.Args[0].Value, PrimitiveType.Str);
				if (arg is not BoundStringLiteral lit)
				{
					result = ErrorExpr(Krn.TypeMismatch, "has_attribute<T>: имя атрибута должно быть строковым литералом (значение вычисляется при компиляции)", c.Span);
					return true;
				}
				result = new BoundBoolLiteral(AttributeNames(decl).Contains(lit.Value), c.Span);
				return true;
			}
		}

		IEnumerable<string> names = name switch
		{
			"field_names" => decl.Fields.Where(f => !f.IsStatic && !f.IsEnumEntry && f.BackingFor is null).Select(f => f.Name),
			"property_names" => decl.Properties.Select(p => p.Name),
			"method_names" => decl.Functions.Where(f => !f.IsAccessor).Select(f => f.Name).Distinct(),
			"parent_names" => decl.Parents.Select(p => p.Type.ToDisplayString()),
			_ => AttributeNames(decl),
		};
		var items = names.Select(n => (BoundExpr)new BoundStringLiteral(n, c.Span)).ToList();
		result = new BoundList(items, new ListType(PrimitiveType.Str), c.Span);
		return true;
	}

	private static List<string> AttributeNames(TypeDeclSymbol decl) =>
		[.. (decl.Syntax?.Attributes ?? []).Select(a => a.Name)];
}
