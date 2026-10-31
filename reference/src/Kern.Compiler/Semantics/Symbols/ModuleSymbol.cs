using Kern.Compiler.Semantics.Scopes;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Symbols;

/// <summary>
/// Модуль — набор файлов с одним именем (`project a.b:` либо имя по пути файла).
/// Экспортируемые символы лежат в <see cref="Scope"/> (типы, функции-группы, глобальные поля).
/// </summary>
public sealed class ModuleSymbol(string name, bool isStd) : Symbol(name)
{
	public override SymbolKind Kind => SymbolKind.Module;
	public bool IsStd { get; } = isStd;
	public List<CompilationUnit> Units { get; } = [];
	/// <summary>Область верхнего уровня: объявления всех файлов модуля.</summary>
	public Scope Scope { get; set; } = null!;
	public List<TypeDeclSymbol> Types { get; } = [];
	public List<FieldSymbol> Fields { get; } = [];
	/// <summary>Все функции верхнего уровня (включая расширения и операторы), по одному символу на перегрузку.</summary>
	public List<FunctionSymbol> Functions { get; } = [];
	public List<PropertySymbol> ExtensionProperties { get; } = [];
	public override string FullName => Name;
}
