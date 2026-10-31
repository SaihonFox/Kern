using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record UnitDeclaration(
	string Name,
	TextSpan Span,
	IReadOnlyList<SyntaxNode>? Members = null,
	IReadOnlyList<ParameterNode>? Parameters = null,
	bool IsImmutable = false,
	Accessibility Accessibility = Accessibility.Public,
	IReadOnlyList<AttributeNode>? Attributes = null) : DeclarationNode(Span)
{
	public IReadOnlyList<SyntaxNode> DeclaredMembers { get; } = Members ?? [];
	public IReadOnlyList<ParameterNode> PrimaryParameters { get; } = Parameters ?? [];
	public IReadOnlyList<AttributeNode> DeclaredAttributes { get; } = Attributes ?? [];

	public IReadOnlyList<GenericParameterNode> GenericParameters { get; init; } = [];
	public IReadOnlyList<GenericParameterNode> DeclaredGenericParameters => GenericParameters;

	// unit не поддерживает наследование (spec §9), поэтому списка родителей здесь нет.
}
