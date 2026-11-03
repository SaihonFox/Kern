using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>
/// Диапазон <c>a..b</c>/<c>a..=b</c>. В позиции среза допускаются открытые границы:
/// <c>..b</c> (<see cref="Start"/> = null), <c>a..</c> (<see cref="End"/> = null) и <c>..</c>.
/// </summary>
public sealed record RangeExpression(
	ExpressionNode? Start,
	ExpressionNode? End,
	bool IsInclusive,
	ExpressionNode? Step,
	TextSpan Span) : ExpressionNode(Span);
