using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Declarations;

/// <summary>
/// Собранный модуль: типы, группы перегрузок верхнеуровневых функций, глобальные поля
/// и реестр расширений по каноническому имени типа-получателя (spec §15).
/// </summary>
public sealed record ModuleSymbol(
	string Name,
	IReadOnlyDictionary<string, TypeSymbol> Types,
	IReadOnlyDictionary<string, IReadOnlyList<FunctionSymbol>> Functions,
	IReadOnlyDictionary<string, FieldSymbol> Globals,
	IReadOnlyDictionary<string, ExtensionGroup> Extensions)
{
	public TypeSymbol? FindType(string name) =>
		Types.TryGetValue(name, out var type) ? type : null;

	public IReadOnlyList<FunctionSymbol> GetFunctions(string name) =>
		Functions.TryGetValue(name, out var group) ? group : [];

	public FieldSymbol? FindGlobal(string name) =>
		Globals.TryGetValue(name, out var field) ? field : null;

	public ExtensionGroup? FindExtensions(KernType receiverType) =>
		Extensions.TryGetValue(receiverType.ToString(), out var group) ? group : null;

	public ExtensionGroup? FindExtensions(string receiverText) =>
		Extensions.TryGetValue(receiverText, out var group) ? group : null;

	/// <summary>
	/// Нативные функции и члены, подключённые к модулю (spec §12): top-level функции
	/// уже добавлены в <see cref="Functions"/>, члены встроенных типов ищутся binder'ом здесь.
	/// </summary>
	public IReadOnlyList<FunctionSymbol> NativeFunctions { get; init; } = [];

	/// <summary>Директивы <c>use</c> модуля в порядке исходника (M3-F).</summary>
	public IReadOnlyList<UseDeclaration> Uses { get; init; } = [];
}
