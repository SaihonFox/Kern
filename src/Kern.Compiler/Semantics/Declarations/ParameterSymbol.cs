using Kern.Compiler.Syntax;
using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Declarations;

/// <summary>
/// Параметр функции, метода или конструктора. Значение по умолчанию хранится синтаксисом:
/// его тип и константность проверяет binder (M3).
/// </summary>
public sealed record ParameterSymbol(
	string Name,
	KernType Type,
	ExpressionNode? DefaultValue,
	bool IsAutoInit,
	bool IsVararg,
	TextSpan Span)
{
	/// <summary>Поле, которое заполняет параметр с <c>$</c> (объявленное или синтезированное).</summary>
	public FieldSymbol? Field { get; init; }

	public bool HasDefault => DefaultValue is not null;
}
