using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public enum LoopExpressionKind
{
	Iteration,
	Condition,
	Infinite,
	DoWhile
}

public sealed record LoopBindingNode(
	TypeSyntax? Type,
	string Name,
	bool IsMutable,
	bool IsIgnored,
	TextSpan Span) : SyntaxNode(Span);

public sealed record LoopExpression(
	LoopExpressionKind Kind,
	bool IsAsync,
	IReadOnlyList<LoopBindingNode> Bindings,
	ExpressionNode? Iterable,
	ExpressionNode? Condition,
	SyntaxNode Body,
	TextSpan Span) : ExpressionNode(Span);
