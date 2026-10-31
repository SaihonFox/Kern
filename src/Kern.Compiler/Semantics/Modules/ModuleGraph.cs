using Kern.Compiler.Semantics.Declarations;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Modules;

/// <summary>
/// Результат разрешения импортов (spec §5): все загруженные модули, импорты каждого модуля и
/// диагностики отсутствующих модулей/символов. Binder использует <see cref="Imports"/>,
/// чтобы искать чужие символы; codegen — чтобы загрузить зависимости.
/// </summary>
public sealed class ModuleGraph(
	IReadOnlyDictionary<string, ModuleSymbol> modules,
	IReadOnlyDictionary<string, IReadOnlyList<ResolvedImport>> imports,
	IReadOnlyList<Diagnostic> diagnostics)
{
	public IReadOnlyDictionary<string, ModuleSymbol> Modules { get; } = modules;

	public IReadOnlyDictionary<string, IReadOnlyList<ResolvedImport>> Imports { get; } = imports;

	public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;

	public ModuleSymbol? FindModule(string name) =>
		Modules.TryGetValue(name, out var module) ? module : null;

	public IReadOnlyList<ResolvedImport> ImportsOf(string moduleName) =>
		Imports.TryGetValue(moduleName, out var imports) ? imports : [];
}
