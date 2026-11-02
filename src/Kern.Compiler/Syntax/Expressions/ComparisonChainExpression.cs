using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>
/// Цепочка сравнений <c>a &lt; b &lt; c</c> (spec §3): раскрывается в
/// <c>a &lt; b &amp;&amp; b &lt; c</c>, но средний операнд вычисляется ровно один раз.
/// Одиночное сравнение остаётся <see cref="BinaryExpression"/>.
/// </summary>
public sealed record ComparisonChainExpression(
	IReadOnlyList<ExpressionNode> Operands,
	IReadOnlyList<string> Operators,
	TextSpan Span) : ExpressionNode(Span);
