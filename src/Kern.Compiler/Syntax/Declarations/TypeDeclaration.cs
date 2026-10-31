using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record TypeDeclaration(
	string Name,
	TextSpan Span,
	IReadOnlyList<SyntaxNode>? Members = null,
	IReadOnlyList<AttributeNode>? Attributes = null,
	bool IsAbstract = false,
	bool IsOpen = false,
	IReadOnlyList<TypeSyntax>? UnionVariants = null,
	bool IsStatic = false,
	Accessibility Accessibility = Accessibility.Public) : DeclarationNode(Span)
{
	public IReadOnlyList<SyntaxNode> DeclaredMembers { get; } = Members ?? [];
	public IReadOnlyList<AttributeNode> DeclaredAttributes { get; } = Attributes ?? [];
	public IReadOnlyList<TypeSyntax> DeclaredUnionVariants { get; } = UnionVariants ?? [];

	public IReadOnlyList<GenericParameterNode> GenericParameters { get; init; } = [];
	public IReadOnlyList<ParameterNode> PrimaryParameters { get; init; } = [];
	public IReadOnlyList<ParentTypeNode> Parents { get; init; } = [];

	/// <summary>Число generic-параметров (для совместимости с таблицей символов).</summary>
	public int GenericArity => GenericParameters.Count;
}
