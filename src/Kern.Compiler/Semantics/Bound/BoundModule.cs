using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Bound;

/// <summary>Параметр привязанной функции: символ + слот.</summary>
public sealed record BoundParameter(ParameterSymbol Symbol, int Slot);

/// <summary>Функция после проверки тела. <see cref="Body"/> всегда блок.</summary>
public sealed record BoundFunction(
	FunctionSymbol Symbol,
	IReadOnlyList<BoundParameter> Parameters,
	BoundBlock Body,
	KernType ReturnType,
	int LocalCount)
{
	public IReadOnlySet<int> BoxedSlots { get; init; } = new HashSet<int>();

	/// <summary>
	/// Инициализаторы полей экземпляра в порядке объявления; заполняются только у конструктора
	/// (spec §8, п.827). Пусто — пролог ничего не генерирует.
	/// </summary>
	public IReadOnlyList<BoundFieldInitializer> Initializers { get; init; } = [];

	/// <summary>
	/// Цепочка <c>: $(...)</c> вторичного конструктора (spec §8, п.829); null — пролог
	/// выполняется в этом конструкторе. Взаимоисключающе с <see cref="Initializers"/>.
	/// </summary>
	public BoundConstructorChain? Chain { get; init; }
}

/// <summary>Результат привязки одного модуля: функции и все диагностики (включая ошибки объявлений).</summary>
public sealed class BoundModule(
	ModuleSymbol symbol,
	IReadOnlyList<BoundFunction> functions,
	IReadOnlyList<Diagnostic> diagnostics)
{
	public ModuleSymbol Symbol { get; } = symbol;

	public IReadOnlyList<BoundFunction> Functions { get; } = functions;

	public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;

	public BoundFunction? FindFunction(string name) =>
		Functions.FirstOrDefault(function => function.Symbol.Name == name);
}
