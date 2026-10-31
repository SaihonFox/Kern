using Kern.Compiler.Semantics.Declare;
using Kern.Compiler.Semantics.Scopes;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Syntax;
using Kern.Core;

namespace Kern.Compiler.Semantics.Project;

/// <summary>Разобранный файл с вычисленным именем модуля (вход <see cref="DeclarationCollector"/>).</summary>
public sealed record SourceUnit(CompilationUnit Unit, string ModuleName, bool IsStd);

/// <summary>
/// Файл после сбора объявлений: AST, модуль и область файла (импорты).
/// Цепочка областей: файл → модуль → prelude. Область файла содержит только импортированные имена.
/// </summary>
public sealed class FileModel(CompilationUnit unit, ModuleSymbol module, Scope scope, bool isStd)
{
	public CompilationUnit Unit { get; } = unit;
	public SourceFile File => Unit.File;
	public ModuleSymbol Module { get; } = module;
	public Scope Scope { get; } = scope;
	public bool IsStd { get; } = isStd;

	/// <summary>Пользовательские модули, импортированные целиком (`use a.b`, `use a.b.*`): видны все их расширения.</summary>
	public List<ModuleSymbol> ImportedModules { get; } = [];
	/// <summary>Расширения-свойства, импортированные по имени (`use a.b[len2]`).</summary>
	public List<PropertySymbol> ImportedExtensionProperties { get; } = [];
}

/// <summary>Результат загрузки проекта и сбора объявлений («часть 1» семантики).</summary>
public sealed class ProjectModel
{
	public required string Name { get; init; }
	/// <summary>Корень проекта (папка kern.toml / исходников); null — проект из памяти.</summary>
	public string? RootDirectory { get; init; }
	/// <summary>Исходная папка с .kern (корень пространств имён).</summary>
	public string? SourceDirectory { get; init; }
	/// <summary>Относительный путь файла с точкой входа (из kern.toml `entry`), если задан.</summary>
	public string? EntryFile { get; init; }

	public required ModuleTable Modules { get; init; }
	public required IReadOnlyList<FileModel> Files { get; init; }
	public required DiagnosticBag Diagnostics { get; init; }
	public required NativeBinder Binder { get; init; }
	public required TypeResolver Resolver { get; init; }
	/// <summary>Область неявных импортов (prelude); родитель областей всех модулей.</summary>
	public required Scope Prelude { get; init; }
	/// <summary>Все объявленные типы (включая вложенные) в порядке объявления.</summary>
	public required IReadOnlyList<TypeDeclSymbol> Types { get; init; }

	public IEnumerable<FileModel> ProjectFiles => Files.Where(f => !f.IsStd);
	public IEnumerable<ModuleSymbol> ProjectModules => Modules.All.Where(m => !m.IsStd);
	public bool HasErrors => Diagnostics.HasErrors;
}
