using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>
/// Родитель в заголовке типа: <c>A</c>, <c>B(x, 0)</c>, <c>async_iter&lt;i32&gt;</c>.
/// <see cref="HasArgumentList"/> отличает <c>[A]</c> от <c>[A()]</c>.
/// </summary>
public sealed record ParentTypeNode(
	TypeSyntax Type,
	IReadOnlyList<ExpressionNode> ConstructorArguments,
	bool HasArgumentList,
	TextSpan Span) : SyntaxNode(Span)
{
	public override string ToString() => HasArgumentList
		? $"{Type}({string.Join(',', ConstructorArguments)})"
		: Type.ToString();
}
