using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Неявные и константные преобразования (spec §3/§9/§15): <c>T? → object</c>, числовое
/// расширение, <c>u8 small = 5</c> для литералов; ошибка — <c>KRN2102</c>.
/// </summary>
public sealed partial class Binder
{
	private BoundExpression Convert(BoundExpression expression, KernType target, TextSpan span)
	{
		if (IsError(expression.Type) || IsError(target) || target.Equals(PrimitiveType.Void))
			return expression;
		if (expression.Type.Equals(target))
			return expression;
		if (Conversions.TryClassify(expression.Type, target, out var conversion))
			return new BoundConversionExpression(expression, conversion, target, span);
		// C#-подобное константное преобразование: `u8 small = 5` (spec §3/§15).
		// Идёт после обычных неявных преобразований, чтобы расширение литерала
		// по-прежнему оставалось NumericWidening.
		if (expression is BoundLiteralExpression { Numeric: { } numeric } &&
			target is PrimitiveType primitive &&
			Conversions.TryClassifyConstant(numeric, primitive.Primitive, out var constant))
		{
			return new BoundConversionExpression(expression, constant, target, span);
		}

		Report("KRN2102", $"Нельзя преобразовать '{expression.Type}' в '{target}'", span);
		return new BoundErrorExpression(span);
	}

	private static BoundExpression ConvertWithConversion(
		BoundExpression expression,
		KernType target,
		Conversion conversion,
		TextSpan span)
	{
		if (conversion.Kind == ConversionKind.Identity || expression.Type.Equals(target))
			return expression;

		return new BoundConversionExpression(expression, conversion, target, span);
	}

	private BoundExpression ConvertWithConversion(BoundExpression expression, KernType target, TextSpan span) =>
		Convert(expression, target, span);
}
