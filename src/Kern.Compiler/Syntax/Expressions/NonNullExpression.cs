using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>
/// Постфиксный <c>!</c> — утверждение «значение не <c>nil</c>» (spec §3/§4): тип
/// <c>T?</c> сужается до <c>T</c>. Во время выполнения отдельная проверка не нужна:
/// использование <c>nil</c> как значения паникует обычным путём.
/// </summary>
public sealed record NonNullExpression(
	ExpressionNode Operand,
	TextSpan Span) : ExpressionNode(Span);
