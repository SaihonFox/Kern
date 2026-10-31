using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Bound;

/// <summary>
/// Конструктор <c>result</c>: <c>Ok(value)</c> или <c>Err(error)</c>. Значение — встроенный
/// unit-тип <see cref="ResultTypes.Ok"/>/<see cref="ResultTypes.Err"/>; кодогенерация создаёт
/// объект и записывает поле напрямую (без синтезированного конструктора).
/// </summary>
public sealed record BoundResultConstructionExpression(
	BoundExpression Value,
	FieldSymbol Field,
	bool IsOk,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);
