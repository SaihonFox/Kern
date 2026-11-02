using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Attributes;

/// <summary>
/// <c>#[allow(KRN0801, KRN2101)]</c> на объявлении (spec §7): предупреждения с
/// перечисленными кодами внутри этого объявления не печатаются. <c>allow(warnings)</c>
/// подавляет все предупреждения объявления; ошибки не подавляются никогда.
/// </summary>
public static class SuppressionAttribute
{
	private sealed record Range(TextSpan Span, HashSet<string> Codes);

	public static IReadOnlyList<Diagnostic> Filter(IEnumerable<Diagnostic> diagnostics, CompilationUnit unit)
	{
		ArgumentNullException.ThrowIfNull(diagnostics);
		ArgumentNullException.ThrowIfNull(unit);

		var ranges = new List<Range>();
		foreach (var declaration in unit.Declarations)
			Collect(declaration, ranges);
		if (ranges.Count == 0)
			return [.. diagnostics];

		var result = new List<Diagnostic>();
		foreach (Diagnostic diagnostic in diagnostics)
		{
			if (diagnostic.IsError || !IsSuppressed(diagnostic, ranges))
				result.Add(diagnostic);
		}
		return result;
	}

	private static bool IsSuppressed(Diagnostic diagnostic, IReadOnlyList<Range> ranges)
	{
		foreach (Range range in ranges)
		{
			if (diagnostic.Span.Start < range.Span.Start || diagnostic.Span.End > range.Span.End)
				continue;
			if (range.Codes.Contains("all") || range.Codes.Contains("warnings") ||
				range.Codes.Contains(diagnostic.Code))
				return true;
		}
		return false;
	}

	private static void Collect(SyntaxNode node, List<Range> ranges)
	{
		IReadOnlyList<AttributeNode> attributes = ConditionalCompilation.AttributesOf(node);
		foreach (AttributeNode attribute in attributes)
		{
			if (!string.Equals(attribute.Name, "allow", StringComparison.Ordinal))
				continue;
			var codes = new HashSet<string>(StringComparer.Ordinal);
			foreach (string argument in attribute.Arguments)
				codes.Add(Unquote(argument));
			ranges.Add(new Range(node.Span, codes));
		}

		switch (node)
		{
			case TypeDeclaration type:
				foreach (var member in type.DeclaredMembers)
					Collect(member, ranges);
				break;
			case UnitDeclaration unit:
				foreach (var member in unit.DeclaredMembers)
					Collect(member, ranges);
				break;
		}
	}

	private static string Unquote(string text) =>
		text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
}
