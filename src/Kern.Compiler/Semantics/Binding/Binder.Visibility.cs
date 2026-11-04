using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Modules;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Видимость имён модуля (M3-F): собственные объявления и нативы, затем импортированные
/// через <c>use</c> (область видимости уже учитывает re-export и конфликты).
/// </summary>
public sealed partial class Binder
{
	/// <summary>Видимые функции: свои (с нативами) плюс импортированные без дублей.</summary>
	private IReadOnlyList<FunctionSymbol> VisibleFunctions(string name)
	{
		var own = _module.GetFunctions(name);
		var imported = _moduleScope.GetFunctions(name);
		if (imported.Count == 0)
			return own;

		var result = new List<FunctionSymbol>(own);
		foreach (var function in imported)
			if (!result.Contains(function))
				result.Add(function);
		return result;
	}

	/// <summary>Видимый тип: собственный (включая нативные) либо импортированный.</summary>
	private TypeSymbol? VisibleType(string name) =>
		_module.FindType(name) ?? _moduleScope.FindType(name);

	private FieldSymbol? VisibleGlobal(string name) =>
		_module.FindGlobal(name) ?? _moduleScope.FindGlobal(name);

	/// <summary>
	/// Модуль, импортированный этим файлом через <c>use</c>, по короткому имени
	/// (<c>use kern.ini[...]</c> → <c>ini</c>). Локальная переменная или тип с тем же
	/// именем важнее: <c>text.upper(...)</c> не перекрывает переменную <c>text</c>.
	/// </summary>
	private ModuleScope? QualifiedModule(string name) =>
		_scope.Lookup(name) is null && VisibleType(name) is null
			? _moduleScope.FindModule(name)
			: null;
}
