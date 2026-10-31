using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record LambdaExpression(
	IReadOnlyList<ParameterNode> Parameters,
	TypeSyntax? ReturnType,
	SyntaxNode Body,
	TextSpan Span) : ExpressionNode(Span)
{
	/// <summary>
	/// Локальная <c>async fun</c> (spec §16): тело возвращает объявленный <c>T</c>,
	/// видимый тип — <c>fun(...)-&gt;task&lt;T&gt;</c>. У анонимных лямбд пока не задаётся.
	/// </summary>
	public bool IsAsync { get; init; }
}
