using Kern.Compiler.Semantics.Attributes;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Modules;

/// <summary>
/// Разрешение `use` (spec §5): загружает модули через переданный загрузчик, собирает их символы
/// и проверяет явно импортируемые имена. Ленивость обеспечивает загрузчик: модуль компилируется
/// только когда он достижим по `use` (stdlib подключается тем же механизмом в M7).
/// </summary>
public sealed class ModuleLoader
{
	private readonly Func<string, CompilationUnit?> _loadUnit;
	private readonly Dictionary<string, ModuleSymbol> _modules = new(StringComparer.Ordinal);
	private readonly Dictionary<string, IReadOnlyList<ResolvedImport>> _imports = new(StringComparer.Ordinal);
	private readonly List<Diagnostic> _diagnostics = [];
	private readonly HashSet<string> _resolved = new(StringComparer.Ordinal);
	private readonly Queue<(string Name, CompilationUnit Unit)> _pending = new();

	public ModuleLoader(Func<string, CompilationUnit?> loadUnit)
	{
		ArgumentNullException.ThrowIfNull(loadUnit);
		_loadUnit = loadUnit;
	}

	public ModuleGraph Resolve(IEnumerable<(string Name, CompilationUnit Unit)> roots)
	{
		ArgumentNullException.ThrowIfNull(roots);

		foreach (var (name, unit) in roots)
		{
			ArgumentNullException.ThrowIfNull(unit);
			var moduleName = string.IsNullOrWhiteSpace(name) ? unit.Project ?? "?" : name;
			EnsureLoaded(moduleName, unit);
		}

		while (_pending.Count > 0)
			ResolveModule(_pending.Dequeue());

		return new ModuleGraph(_modules, _imports, _diagnostics);
	}

	private ModuleSymbol EnsureLoaded(string name, CompilationUnit unit)
	{
		if (_modules.TryGetValue(name, out var existing))
			return existing;

		var (table, tableDiagnostics) = SymbolTable.Collect(unit);
		_diagnostics.AddRange(tableDiagnostics);

		var (module, collectorDiagnostics) = DeclarationCollector.Collect(unit, table, name);
		_diagnostics.AddRange(collectorDiagnostics);

		_modules.Add(name, module);
		_pending.Enqueue((name, unit));
		return module;
	}

	private void ResolveModule((string Name, CompilationUnit Unit) pending)
	{
		if (!_resolved.Add(pending.Name))
			return;

		var imports = new List<ResolvedImport>();
		foreach (var declaration in pending.Unit.Declarations.OfType<UseDeclaration>().Where(ConditionalCompilation.IsIncluded))
		{
			var targetName = declaration.Path;
			if (!_modules.TryGetValue(targetName, out var target))
			{
				var unit = _loadUnit(targetName);
				if (unit is null)
				{
					_diagnostics.Add(new Diagnostic(
						"KRN1101",
						$"Модуль '{targetName}' не найден. Проверьте путь `use`; внешние библиотеки должны быть собраны заранее.",
						declaration.Span));
					continue;
				}

				target = EnsureLoaded(targetName, unit);
			}

			foreach (var item in declaration.ImportItems)
			{
				if (!Contains(target, item))
					_diagnostics.Add(new Diagnostic(
						"KRN1102",
						$"Импортируемый символ '{item}' не найден в модуле '{targetName}'",
						declaration.Span));
			}

			imports.Add(new ResolvedImport(target, declaration.ImportItems, declaration.IsWildcard, declaration.Span));
		}

		_imports[pending.Name] = imports;
	}

	private static bool Contains(ModuleSymbol module, string name) =>
		module.Types.ContainsKey(name) ||
		module.Functions.ContainsKey(name) ||
		module.Globals.ContainsKey(name) ||
		module.Extensions.Values.Any(group =>
			group.Functions.Any(function => function.Name == name) ||
			group.Properties.Any(property => property.Name == name));
}
