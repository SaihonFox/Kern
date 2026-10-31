using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Overloads;

/// <summary>
/// Аргумент, привязанный к параметру выбранной перегрузки. Для значения по умолчанию
/// <see cref="Argument"/> равен null, а преобразование — identity (binder вставит узел значения по умолчанию).
/// </summary>
public sealed record BoundArgument(
	ParameterSymbol Parameter,
	CallArgument? Argument,
	Conversion Conversion,
	int ParameterIndex,
	bool IsExpanded);

/// <summary>Результат выбора перегрузки (spec §15).</summary>
public sealed class OverloadResolution
{
	public FunctionSymbol? Function { get; init; }

	/// <summary>Аргументы в порядке параметров, включая значения по умолчанию.</summary>
	public IReadOnlyList<BoundArgument> Arguments { get; init; } = [];

	/// <summary>Выведенные или явные generic-аргументы в порядке объявления.</summary>
	public IReadOnlyList<KernType> TypeArguments { get; init; } = [];

	public Conversion? ReceiverConversion { get; init; }

	public KernType? ReturnType { get; init; }

	public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

	public bool Success => Function is not null && Diagnostics.Count == 0;
}
