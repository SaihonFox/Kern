using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>
/// Аргумент вызова с явным маркером: <c>&amp;x</c> — передача в <c>in</c>-параметр
/// (<c>&amp;T</c>), <c>*x</c> — в <c>ref</c>-параметр (<c>*T</c>, spec §6).
/// Маркер разбирается только на месте аргумента, поэтому не конфликтует с битовым
/// <c>&amp;</c> и умножением <c>*</c>.
/// </summary>
public sealed record ReferenceArgumentExpression(
	ExpressionNode Operand,
	bool Mutable,
	TextSpan Span) : ExpressionNode(Span);
