using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record ExtensionOperatorDeclaration(
	TypeSyntax ReceiverType,
	string Operator,
	IReadOnlyList<ParameterNode> Parameters,
	TypeSyntax? ReturnType,
	SyntaxNode? Body,
	TextSpan Span,
	Accessibility Accessibility = Accessibility.Public,
	IReadOnlyList<AttributeNode>? Attributes = null) : DeclarationNode(Span)
{
	public IReadOnlyList<AttributeNode> DeclaredAttributes { get; } = Attributes ?? [];
}
