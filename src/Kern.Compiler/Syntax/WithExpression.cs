using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>Замена одного поля в <c>with</c>-выражении (spec round 27).</summary>
public sealed record WithAssignmentNode(
	string Name,
	ExpressionNode Value,
	TextSpan Span) : SyntaxNode(Span);

/// <summary>
/// <c>x:{ a = 1, b = 2 }</c> (spec round 27): копия <c>unit</c>/<c>type</c> с заменёнными
/// полями. Скобки и <c>:</c> пишутся слитно, нового ключевого слова нет.
/// </summary>
public sealed record WithExpression(
	ExpressionNode Target,
	IReadOnlyList<WithAssignmentNode> Assignments,
	TextSpan Span) : ExpressionNode(Span);
