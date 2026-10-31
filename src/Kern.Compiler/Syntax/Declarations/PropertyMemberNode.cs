using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record PropertyMemberNode(
	TypeSyntax Type,
	string Name,
	IReadOnlyList<PropertyAccessorNode> Accessors,
	ExpressionNode? ExpressionBody,
	ExpressionNode? Initializer,
	TextSpan Span,
	bool IsStatic = false,
	Accessibility Accessibility = Accessibility.Private,
	bool IsOverride = false) : SyntaxNode(Span), ITypeMember
{
	/// <summary>
	/// Автосвойство: все accessor'ы без тела и нет выражения-тела
	/// (<c>i32 val { get; set }</c>, spec §16).
	/// </summary>
	public bool IsAuto => Accessors.Count > 0 &&
		Accessors.All(accessor => accessor.Body is null) && ExpressionBody is null;
}
