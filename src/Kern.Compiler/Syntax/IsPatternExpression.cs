using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>
/// <c>x is Pattern</c> / <c>x !is Pattern</c> (spec round 27): справа — полноценный паттерн
/// <c>match</c>, а не только имя типа. Связывания видны справа от <c>&amp;&amp;</c> и в
/// then-ветви <c>if</c>.
/// </summary>
public sealed record IsPatternExpression(
	ExpressionNode Subject,
	PatternNode Pattern,
	bool Negated,
	TextSpan Span) : ExpressionNode(Span);
