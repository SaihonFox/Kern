using Kern.Compiler.Semantics.Declarations;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Bound;

/// <summary>
/// Цепочка конструктора <c>: $(...)</c> после выбора перегрузки (spec §8, п.829): вызываемый
/// конструктор того же типа и аргументы в порядке его параметров. Конструктор с цепочкой
/// инициализаторы не выполняет — это делает корень цепочки.
/// </summary>
public sealed record BoundConstructorChain(
	FunctionSymbol Target,
	IReadOnlyList<BoundExpression> Arguments,
	TextSpan Span);
