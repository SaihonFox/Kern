using Kern.Compiler.Semantics.Declarations;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Bound;

/// <summary>
/// Вызов конструктора прямого родителя в прологе корневого конструктора наследника
/// (slice A1, spec §8). <see cref="Target"/> — выбранный конструктор родителя; приёмником
/// служит тот же объект (<c>$</c> в слоте 0). Аргументы — <c>[A(x)]</c> из заголовка типа,
/// для <c>[A]</c> список пуст (<c>[A()]</c>).
/// </summary>
public sealed record BoundBaseConstructorCall(
	FunctionSymbol Target,
	IReadOnlyList<BoundExpression> Arguments,
	TextSpan Span);
