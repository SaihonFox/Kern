using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record LocalDeclarationStatement(
	TypeSyntax? Type,
	string Name,
	ExpressionNode? Initializer,
	bool IsMutable,
	TextSpan Span,
	IReadOnlyList<LocalBindingNode>? AdditionalBindings = null) : StatementNode(Span)
{
	public IReadOnlyList<LocalBindingNode> DeclaredBindings { get; } = AdditionalBindings ?? [];

	/// <summary>
	/// Маркер single-assignment локальной переменной (<c>i32! num</c> / <c>var! value</c>).
	/// Каноническое хранилище флага — здесь; у явного типа маркер также остаётся в <see cref="TypeSyntax"/>.
	/// </summary>
	public bool IsSingleAssignment { get; init; }
}
