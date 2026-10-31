using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record UseDeclaration(
	string Path,
	TextSpan Span,
	IReadOnlyList<string>? Items = null,
	bool IsWildcard = false) : DeclarationNode(Span)
{
	/// <summary>Явно импортируемые имена; пустой список для `use module` и `use module.*`.</summary>
	public IReadOnlyList<string> ImportItems => Items ?? [];
}
