using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Встроенный оператор повтора (spec §3): <c>str * inum</c>, <c>char * inum</c>,
/// <c>list&lt;T&gt; * inum</c> в обоих порядках. Число приводится к <c>i64</c>; результат —
/// <c>str</c> для строки/символа и тот же <c>list&lt;T&gt;</c> для списка.
/// </summary>
public sealed partial class Binder
{
	private BoundExpression? TryRepeat(BoundExpression left, BoundExpression right, TextSpan span)
	{
		if (IsRepeatable(left.Type) && IsInteger(right.Type))
			return new BoundBinaryExpression(
				left,
				"*",
				ConvertWithConversion(right, PrimitiveType.I64, span),
				RepeatResult(left.Type),
				span);
		if (IsInteger(left.Type) && IsRepeatable(right.Type))
			return new BoundBinaryExpression(
				ConvertWithConversion(left, PrimitiveType.I64, span),
				"*",
				right,
				RepeatResult(right.Type),
				span);
		return null;
	}

	private static KernType RepeatResult(KernType type) =>
		type.Equals(PrimitiveType.Char) ? PrimitiveType.Str : type;

	private static bool IsRepeatable(KernType type) =>
		type.Equals(PrimitiveType.Str) || type.Equals(PrimitiveType.Char) ||
		type is BuiltinGenericType { BuiltinKind: BuiltinGenericKind.List };
}
