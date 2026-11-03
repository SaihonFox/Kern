using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record CompilationUnit(IReadOnlyList<DeclarationNode> Declarations, TextSpan Span) : SyntaxNode(Span)
{
	public string? Project { get; init; }

	/// <summary>Атрибуты модуля: <c>#[module(...)]</c> в начале файла или перед <c>project</c>.</summary>
	public IReadOnlyList<AttributeNode> Attributes { get; init; } = [];
}
