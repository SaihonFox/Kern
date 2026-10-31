using Kern.Compiler.Lexing;
using Kern.Compiler.Semantics.Types;
using Kern.Core;

namespace Kern.Compiler.Semantics.Bound.Expressions;

/// <summary>
/// Числовой литерал. <see cref="BoundNode.Type"/> — итоговый точный тип (по суффиксу или по целевому типу
/// после константного приведения); <see cref="Value"/> — исходное значение лексера.
/// </summary>
public sealed record BoundNumberLiteral(NumberValue Value, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);

public sealed record BoundStringLiteral(string Value, TextSpan Span) : BoundExpr(PrimitiveType.Str, Span);

/// <summary>Символ; <see cref="CodePoint"/> — кодовая точка Unicode.</summary>
public sealed record BoundCharLiteral(int CodePoint, TextSpan Span) : BoundExpr(PrimitiveType.Char, Span);

public sealed record BoundBoolLiteral(bool Value, TextSpan Span) : BoundExpr(PrimitiveType.Bool, Span);

/// <summary>`nil`. Тип — <see cref="NilType"/>; при приведении к T? оборачивается в <see cref="BoundConversion"/> (NilToNullable).</summary>
public sealed record BoundNilLiteral(TextSpan Span) : BoundExpr(NilType.Instance, Span);

/// <summary>`none` — «значение ещё не присвоено» (используется в `x == none`).</summary>
public sealed record BoundNoneLiteral(TextSpan Span) : BoundExpr(NoneType.Instance, Span);

/// <summary>Часть интерполированной строки (не выражение).</summary>
public abstract record BoundInterpolationPart(TextSpan Span) : BoundNode(PrimitiveType.Str, Span);

public sealed record BoundInterpolationText(string Text, TextSpan Span) : BoundInterpolationPart(Span);

/// <summary>`${value}`; <see cref="Debug"/> — `${x=}`, <see cref="Format"/> — `${v:.2f}`, <see cref="SourceText"/> — текст выражения для debug-вывода.</summary>
public sealed record BoundInterpolationValue(BoundExpr Value, bool Debug, string? Format, string SourceText, TextSpan Span)
	: BoundInterpolationPart(Span);

/// <summary>`f"..${x}.."`. Тип — str.</summary>
public sealed record BoundInterpolatedString(IReadOnlyList<BoundInterpolationPart> Parts, TextSpan Span) : BoundExpr(PrimitiveType.Str, Span);

/// <summary>Заглушка после ошибки привязки (тип — <see cref="ErrorType"/>, каскадных диагностик не порождает).</summary>
public sealed record BoundErrorExpr(TextSpan Span, string? Message = null) : BoundExpr(ErrorType.Instance, Span);
