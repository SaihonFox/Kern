using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public enum LoopExpressionKind
{
	Iteration,
	Condition,
	Infinite,
	DoWhile
}

/// <summary>Метка цикла <c>loop@outer</c> (spec §3, раунд 27): имя и span <c>@outer</c>.</summary>
public sealed record LoopLabel(string Name, TextSpan Span);

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
	TextSpan Span,
	LoopLabel? Label = null) : ExpressionNode(Span);
