using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;

namespace Kern.Compiler.Semantics.Bound.Expressions;

/// <summary>
/// Захваченная лямбдой внешняя переменная (локальная или параметр объемлющей функции/лямбды).
/// <see cref="IsMutated"/> — переменная где-либо присваивается после захвата (или внутри лямбды): такая переменная
/// должна храниться в общей ячейке (boxed), иначе достаточно копии значения.
/// </summary>
public sealed record BoundCapture(Symbol Variable, TypeSymbol Type, bool IsMutated, TextSpan FirstUse)
{
	public LocalSymbol? Local => Variable as LocalSymbol;
	public ParameterSymbol? Parameter => Variable as ParameterSymbol;
}

/// <summary>
/// Function expression. <see cref="Type"/> — <see cref="FuncType"/> (параметры → возвращаемый тип; для async — task&lt;R&gt;).
/// <see cref="Captures"/> — внешние переменные, <see cref="CapturesThis"/> — используется `$` объемлющего метода.
/// Вложенные лямбды: захват внутренней, ссылающийся на внешнюю переменную, попадает и в Captures внешней.
/// </summary>
public sealed record BoundLambda(
	IReadOnlyList<ParameterSymbol> Parameters,
	BoundExpr Body,
	IReadOnlyList<BoundCapture> Captures,
	bool CapturesThis,
	bool IsAsync,
	TypeSymbol Type,
	TextSpan Span) : BoundExpr(Type, Span)
{
	/// <summary>Тип лямбды как функции (null только если биндер передал не-FuncType, т.е. тип — ошибка).</summary>
	public FuncType? Signature => Type as FuncType;
	public TypeSymbol ReturnType => Signature?.Return ?? ErrorType.Instance;
}
