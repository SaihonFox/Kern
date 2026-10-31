using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record GlobalFieldDeclaration(
	TypeSyntax Type,
	string Name,
	bool IsRequired,
	ExpressionNode? Initializer,
	TextSpan Span,
	bool IsInferred = false,
	Accessibility Accessibility = Accessibility.Public,
	IReadOnlyList<AttributeNode>? Attributes = null) : DeclarationNode(Span)
{
	public IReadOnlyList<AttributeNode> DeclaredAttributes { get; } = Attributes ?? [];
}
