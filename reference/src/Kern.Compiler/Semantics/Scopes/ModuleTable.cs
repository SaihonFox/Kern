using Kern.Compiler.Semantics.Symbols;

namespace Kern.Compiler.Semantics.Scopes;

/// <summary>Все модули компиляции по имени (проект + std).</summary>
public sealed class ModuleTable
{
	private readonly Dictionary<string, ModuleSymbol> _modules = [];

	public IEnumerable<ModuleSymbol> All => _modules.Values;
	public ModuleSymbol? Find(string name) => _modules.GetValueOrDefault(name);

	public ModuleSymbol GetOrAdd(string name, bool isStd)
	{
		if (_modules.TryGetValue(name, out var m)) return m;
		m = new ModuleSymbol(name, isStd);
		_modules[name] = m;
		return m;
	}
}
