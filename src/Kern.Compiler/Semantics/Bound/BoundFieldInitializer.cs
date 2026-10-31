using Kern.Compiler.Semantics.Declarations;

namespace Kern.Compiler.Semantics.Bound;

/// <summary>
/// Инициализатор поля экземпляра (в том числе backing-поля автосвойства): поле и выражение.
/// Пролог конструктора выполняет их в порядке объявления до <c>$</c>-присваиваний (spec §8, п.827).
/// </summary>
public sealed record BoundFieldInitializer(FieldSymbol Field, BoundExpression Value);
