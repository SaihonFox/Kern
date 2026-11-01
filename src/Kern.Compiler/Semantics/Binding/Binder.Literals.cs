using Kern.Binding.Formatting;
using Kern.Compiler.Lexing;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

public sealed partial class Binder
{
	/// <summary>
	/// Интерполяция строки (spec §3): каждый кусок сводится к <c>str</c> (текст или
	/// <c>format(value[, spec])</c>), затем куски склеиваются оператором <c>+</c>.
	/// Для <c>${expr=}</c> перед значением ставится текст <c>expr=</c>.
	/// </summary>
	private BoundExpression BindInterpolatedString(InterpolatedStringExpression expression)
	{
		var parts = new List<BoundExpression>();
		foreach (InterpolationPartNode part in expression.Parts)
		{
			switch (part)
			{
				case InterpolationTextPart text when text.Text.Length > 0:
					parts.Add(new BoundLiteralExpression(text.Text, null, PrimitiveType.Str, text.Span));
					break;

				case InterpolationExpressionPart interpolation:
					BindInterpolationExpressionPart(interpolation, parts);
					break;
			}
		}

		BoundExpression result;
		if (parts.Count == 0)
		{
			result = new BoundLiteralExpression(string.Empty, null, PrimitiveType.Str, expression.Span);
		}
		else
		{
			result = parts[0];
			for (var index = 1; index < parts.Count; index++)
				result = new BoundBinaryExpression(result, "+", parts[index], PrimitiveType.Str, expression.Span);
		}

		return expression.Regex ? BindRegexCall(result, expression.Span) : result;
	}

	private void BindInterpolationExpressionPart(
		InterpolationExpressionPart part,
		List<BoundExpression> parts)
	{
		if (part.Expression is null)
		{
			Report("KRN2101", "Пустое выражение в интерполяции", part.Span);
			return;
		}

		var value = BindExpression(part.Expression);
		if (IsError(value.Type))
			return;

		if (part.Debug)
			parts.Add(new BoundLiteralExpression(
				(part.DebugText ?? string.Empty) + "=", null, PrimitiveType.Str, part.Span));

		var arity = part.Spec is null ? 1 : 2;
		if (FormatFunction(arity) is not { } format)
		{
			Report("KRN2101", "Встроенная функция 'format' недоступна для интерполяции", part.Span);
			return;
		}

		var arguments = new List<BoundExpression>(arity) { value };
		if (part.Spec is not null)
		{
			if (!KernFormat.TryValidateSpec(part.Spec, out var specError))
				Report(
					"KRN2101",
					$"Некорректная спецификация формата '{part.Spec}': {specError}",
					part.Span);
			arguments.Add(new BoundLiteralExpression(part.Spec, null, PrimitiveType.Str, part.Span));
		}

		parts.Add(new BoundCallExpression(format, arguments, PrimitiveType.Str, part.Span));
	}

	private FunctionSymbol? FormatFunction(int arity)
	{
		foreach (FunctionSymbol function in VisibleFunctions("format"))
			if (function.NativeKey is not null && function.NativeSelfType is null &&
				function.Parameters.Count == arity)
				return function;
		foreach (FunctionSymbol function in BuiltinCatalog)
			if (function.Name == "format" && function.Parameters.Count == arity)
				return function;
		return null;
	}

	/// <summary>Нативная свободная функция с именем и числом параметров (например, <c>to_debug</c>).</summary>
	private FunctionSymbol? NativeGlobalFunction(string name, int arity)
	{
		foreach (FunctionSymbol function in VisibleFunctions(name))
			if (function.NativeKey is not null && function.NativeSelfType is null &&
				function.Parameters.Count == arity)
				return function;
		return null;
	}

	/// <summary>Строковый литерал: lexer отдаёт текст с префиксом и кавычками, binder снимает их (spec §3).</summary>
	private BoundExpression BindStringLiteral(string text, TextSpan span)
	{
		if (!StringLiteralText.TryDecode(text, out var value, out var error))
		{
			Report("KRN2101", error ?? "некорректный строковый литерал", span);
			return new BoundLiteralExpression(string.Empty, null, PrimitiveType.Str, span);
		}

		var literal = new BoundLiteralExpression(value, null, PrimitiveType.Str, span);
		// `x"…"` — regex-значение: литерал шаблона превращается в вызов `regex(pattern)`.
		return StringLiteralText.TryReadHeader(text, out var header, out _) && header.Regex
			? BindRegexCall(literal, span)
			: literal;
	}

	/// <summary>Обёртка regex-литерала: `regex(pattern)` из `kern.regex` (spec §12).</summary>
	private BoundExpression BindRegexCall(BoundExpression pattern, TextSpan span)
	{
		if (IsError(pattern.Type))
			return new BoundErrorExpression(span);

		FunctionSymbol? regex = null;
		foreach (var candidate in VisibleFunctions("regex"))
			if (candidate.Parameters.Count == 1)
			{
				regex = candidate;
				break;
			}

		if (regex is null)
		{
			Report("KRN2103", "Regex-литерал требует функции 'regex' из kern.regex", span);
			return new BoundErrorExpression(span);
		}

		var converted = Convert(pattern, regex.Parameters[0].Type, span);
		if (IsError(converted.Type))
			return new BoundErrorExpression(span);
		return new BoundCallExpression(
			regex,
			[converted],
			regex.ReturnType ?? PrimitiveType.Object,
			span);
	}

	private BoundExpression BindCharLiteral(string text, TextSpan span)
	{
		if (!StringLiteralText.TryDecodeChar(text, out var value, out var error))
		{
			Report("KRN2101", error ?? "некорректный char-литерал", span);
			return new BoundLiteralExpression('\0', null, PrimitiveType.Char, span);
		}

		return new BoundLiteralExpression(value, null, PrimitiveType.Char, span);
	}

	private BoundExpression BindNumericLiteral(string text, TextSpan span)
	{
		if (!NumericLiteralValue.TryParse(text, out var numeric))
		{
			Report("KRN2101", $"Некорректный числовой литерал '{text}'", span);
			return new BoundErrorExpression(span);
		}

		var type = NumericDefaultType(numeric, span);
		if (type is ErrorType)
			return new BoundErrorExpression(span);

		object value = numeric.IsFloat ? numeric.FloatValue : numeric.IntegerValue;
		return new BoundLiteralExpression(value, numeric, type, span);
	}

	private KernType NumericDefaultType(NumericLiteralValue numeric, TextSpan span)
	{
		if (numeric.ExactType is { } exact)
			return PrimitiveType.For(exact);

		if (numeric.IsFloat)
			return PrimitiveType.F64;

		if (NumericRules.FitsInteger(numeric.IntegerValue, PrimitiveKind.I32))
			return PrimitiveType.I32;
		if (NumericRules.FitsInteger(numeric.IntegerValue, PrimitiveKind.I64))
			return PrimitiveType.I64;
		if (NumericRules.FitsInteger(numeric.IntegerValue, PrimitiveKind.U64))
			return PrimitiveType.U64;

		Report("KRN2101", "Числовой литерал выходит за диапазон u64", span);
		return ErrorType.Instance;
	}
}
