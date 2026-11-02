using Kern.Binding.Catalog;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Native;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Attributes;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Modules;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Kern.Core.Text;

namespace Kern.Compiler.Compilation;

/// <summary>Исходник проекта: путь относительно корня и текст.</summary>
public sealed record ProjectSourceFile(string RelativePath, string Text);

/// <summary>Диагностика с путём файла (для CLI).</summary>
public sealed record ProjectDiagnostic(string Path, Diagnostic Diagnostic);

/// <summary>Результат компиляции проекта: один bound-модуль для codegen и диагностика по файлам.</summary>
public sealed class ProjectCompileResult
{
	public required SourceBundle Bundle { get; init; }

	public BoundModule? Module { get; init; }

	public IReadOnlyList<ProjectDiagnostic> Diagnostics { get; init; } = [];

	public IReadOnlyList<string> NativeProblems { get; init; } = [];

	public bool HasEntryPoint { get; init; }

	public bool Success =>
		Module is not null && NativeProblems.Count == 0 &&
		!Diagnostics.Any(item => item.Diagnostic.IsError);
}

/// <summary>
/// Компиляция проекта из нескольких модулей (M3-F): модуль на файл, имя из <c>project a.b:</c>
/// или пути, разрешение <c>use</c>, привязка каждого модуля в своей области видимости и
/// слияние функций в одну программу. Точка входа — единственный <c>main</c> (обычно
/// корневой модуль); проект без <c>main</c> — библиотека.
/// </summary>
public static class ProjectCompiler
{
	public static ProjectCompileResult Compile(
		IReadOnlyList<ProjectSourceFile> files,
		string projectName,
		NativeCatalog catalog)
	{
		ArgumentNullException.ThrowIfNull(files);
		ArgumentNullException.ThrowIfNull(projectName);
		ArgumentNullException.ThrowIfNull(catalog);

		var diagnostics = new List<ProjectDiagnostic>();
		var bundles = files.Select(file => (file.RelativePath, file.Text)).ToList();
		var bundle = SourceBundle.Create(bundles);

		var parsedFiles = new List<ParsedFile>();
		var parsedSources = new List<(
			ProjectSourceFile File,
			CompilationUnit Tree,
			SymbolTable Table,
			string ModuleName,
			IReadOnlyDictionary<DeclarationNode, TypeSymbol> Primes,
			Dictionary<string, TypeSymbol> Types)>();
		foreach (var file in files)
		{
			var parsed = Parser.Parse(file.Text);
			AddDiagnostics(diagnostics, file.RelativePath, parsed.Diagnostics);
			var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
			AddDiagnostics(diagnostics, file.RelativePath, tableDiagnostics);
			var moduleName = parsed.Tree.Project ?? ModuleNameFromPath(file.RelativePath);
			var (primes, types) = CreateSymbols(parsed.Tree);
			parsedSources.Add((file, parsed.Tree, table, moduleName, primes, types));
		}

		if (HasErrors(diagnostics))
			return new ProjectCompileResult { Bundle = bundle, Diagnostics = diagnostics };

		var typesByModule = parsedSources.ToDictionary(
			source => source.ModuleName,
			source => source.Types,
			StringComparer.Ordinal);
		var orderedSources = OrderByImports(parsedSources);
		var nativeTypes = NativeSymbolBuilder.CreateTypeSymbols(catalog);

		foreach (var source in orderedSources)
		{
			var external = BuildExternalTypes(source.Tree, typesByModule);
			var (declaredModule, collectorDiagnostics) = DeclarationCollector.Collect(
				source.Tree,
				source.Table,
				source.ModuleName,
				source.Primes,
				name => external.TryGetValue(name, out var importedType) ? importedType : null,
				nativeTypes);
			AddDiagnostics(diagnostics, source.File.RelativePath, collectorDiagnostics);
			var imported = NativeSymbolBuilder.Import(declaredModule, catalog, out var problems);
			parsedFiles.Add(new ParsedFile(source.File.RelativePath, source.Table, imported, problems));
		}

		if (HasErrors(diagnostics))
			return new ProjectCompileResult { Bundle = bundle, Diagnostics = diagnostics };

		var nativeProblems = parsedFiles.SelectMany(file => file.NativeProblems).ToList();
		if (nativeProblems.Count > 0)
			return new ProjectCompileResult { Bundle = bundle, Diagnostics = diagnostics, NativeProblems = nativeProblems };

		var modules = parsedFiles.Select(file => file.Module).ToList();
		var (scopes, moduleDiagnostics) = ModuleResolver.Resolve(modules);
		var pathByModule = parsedFiles
			.GroupBy(file => file.Module.Name, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.First().Path, StringComparer.Ordinal);
		foreach (var diagnostic in moduleDiagnostics)
		{
			var path = pathByModule.TryGetValue(diagnostic.ModuleName, out var known) ? known : diagnostic.ModuleName;
			diagnostics.Add(new ProjectDiagnostic(path, diagnostic.Diagnostic));
		}

		if (HasErrors(diagnostics))
			return new ProjectCompileResult { Bundle = bundle, Diagnostics = diagnostics };

		var entry = FindEntryModule(parsedFiles, diagnostics);
		if (HasErrors(diagnostics))
			return new ProjectCompileResult { Bundle = bundle, Diagnostics = diagnostics };

		// Точка входа привязывается последней: codegen фиксирует EntryPoint по первому main.
		var ordered = parsedFiles
			.OrderBy(file => ReferenceEquals(file, entry) ? 1 : 0)
			.ToList();

		var boundFunctions = new List<BoundFunction>();
		foreach (var file in ordered)
		{
			var scope = scopes[file.Module.Name];
			var bound = new Binder(scope, file.Table).Bind();
			AddDiagnostics(diagnostics, file.Path, bound.Diagnostics);
			boundFunctions.AddRange(bound.Functions);
		}

		var staticInitializers = boundFunctions.Count(function => function.Symbol.Name == "<static-init>" && function.Symbol.Owner is null);
		if (staticInitializers > 1)
		{
			diagnostics.Add(new ProjectDiagnostic(
				ordered[0].Path,
				new Diagnostic(
					"KRN1014",
					"Статические инициализаторы в нескольких модулях пока не поддержаны",
					default)));
		}

		if (HasErrors(diagnostics))
			return new ProjectCompileResult { Bundle = bundle, Diagnostics = diagnostics };

		var symbol = MergeSymbol(projectName, parsedFiles);
		var module = new BoundModule(symbol, boundFunctions, []);
		return new ProjectCompileResult
		{
			Bundle = bundle,
			Module = module,
			Diagnostics = FilterSuppressions(diagnostics, parsedSources),
			HasEntryPoint = entry is not null,
		};
	}

	/// <summary>Применить <c>#[allow(...)]</c> каждого файла к его предупреждениям.</summary>
	private static List<ProjectDiagnostic> FilterSuppressions(
		List<ProjectDiagnostic> diagnostics,
		List<(
			ProjectSourceFile File,
			CompilationUnit Tree,
			SymbolTable Table,
			string ModuleName,
			IReadOnlyDictionary<DeclarationNode, TypeSymbol> Primes,
			Dictionary<string, TypeSymbol> Types)> sources)
	{
		var trees = sources
			.GroupBy(source => source.File.RelativePath, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.First().Tree, StringComparer.Ordinal);
		var result = new List<ProjectDiagnostic>(diagnostics.Count);
		foreach (var diagnostic in diagnostics)
		{
			if (diagnostic.Diagnostic.IsError || !trees.TryGetValue(diagnostic.Path, out var tree))
			{
				result.Add(diagnostic);
				continue;
			}

			if (SuppressionAttribute.Filter([diagnostic.Diagnostic], tree).Count > 0)
				result.Add(diagnostic);
		}
		return result;
	}

	/// <summary>
	/// Порядок сборки: зависимости (импортируемые модули) раньше импортёров, чтобы их
	/// символы типов уже были заполнены. Циклы обрабатываются как есть (порядок файлов).
	/// </summary>
	private static List<(
		ProjectSourceFile File,
		CompilationUnit Tree,
		SymbolTable Table,
		string ModuleName,
		IReadOnlyDictionary<DeclarationNode, TypeSymbol> Primes,
		Dictionary<string, TypeSymbol> Types)> OrderByImports(
			List<(
				ProjectSourceFile File,
				CompilationUnit Tree,
				SymbolTable Table,
				string ModuleName,
				IReadOnlyDictionary<DeclarationNode, TypeSymbol> Primes,
				Dictionary<string, TypeSymbol> Types)> sources)
	{
		var byName = sources
			.GroupBy(source => source.ModuleName, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
		var ordered = new List<(
			ProjectSourceFile File,
			CompilationUnit Tree,
			SymbolTable Table,
			string ModuleName,
			IReadOnlyDictionary<DeclarationNode, TypeSymbol> Primes,
			Dictionary<string, TypeSymbol> Types)>();
		var visited = new HashSet<string>(StringComparer.Ordinal);
		var visiting = new HashSet<string>(StringComparer.Ordinal);

		void Visit(string moduleName)
		{
			if (!byName.TryGetValue(moduleName, out var source) || !visiting.Add(moduleName))
				return;
			foreach (var use in source.Tree.Declarations.OfType<UseDeclaration>().Where(ConditionalCompilation.IsIncluded))
			{
				if (use.Path.StartsWith("kern.", StringComparison.Ordinal))
					continue;
				if (use.Path.StartsWith("dotnet.", StringComparison.Ordinal))
					continue;
				Visit(use.Path);
			}

			visiting.Remove(moduleName);
			if (visited.Add(moduleName))
				ordered.Add(source);
		}

		foreach (var source in sources)
			Visit(source.ModuleName);
		return ordered;
	}

	private sealed record ParsedFile(
		string Path,
		SymbolTable Table,
		ModuleSymbol Module,
		IReadOnlyList<string> NativeProblems);

	/// <summary>Предварительные символы типов файла: одна идентичность на всё проектное слияние.</summary>
	private static (
		IReadOnlyDictionary<DeclarationNode, TypeSymbol> Primes,
		Dictionary<string, TypeSymbol> Types) CreateSymbols(CompilationUnit unit)
	{
		var primes = new Dictionary<DeclarationNode, TypeSymbol>(ReferenceEqualityComparer.Instance);
		var types = new Dictionary<string, TypeSymbol>(StringComparer.Ordinal);
		foreach (var declaration in unit.Declarations)
		{
			TypeSymbol? symbol = declaration switch
			{
				TypeDeclaration type => new TypeSymbol
				{
					Name = type.Name,
					Kind = type.DeclaredUnionVariants.Count > 0
						? Kern.Compiler.Semantics.Types.SymbolKind.UnionAlias
						: type.IsAbstract
							? Kern.Compiler.Semantics.Types.SymbolKind.AbstractClass
							: Kern.Compiler.Semantics.Types.SymbolKind.Class,
					GenericParameterNames = [.. type.GenericParameters.Select(parameter => parameter.Name)],
					Declaration = type
				},
				UnitDeclaration unitType => new TypeSymbol
				{
					Name = unitType.Name,
					Kind = unitType.IsImmutable
						? Kern.Compiler.Semantics.Types.SymbolKind.ImmutableUnit
						: Kern.Compiler.Semantics.Types.SymbolKind.Unit,
					GenericParameterNames = [.. unitType.GenericParameters.Select(parameter => parameter.Name)],
					Declaration = unitType
				},
				_ => null
			};

			if (symbol is null)
				continue;
			primes[declaration] = symbol;
			types[symbol.Name] = symbol;
		}

		return (primes, types);
	}

	/// <summary>Типы, видимые файлу по его <c>use</c>-директивам (для сборки объявлений).</summary>
	private static Dictionary<string, TypeSymbol> BuildExternalTypes(
		CompilationUnit unit,
		IReadOnlyDictionary<string, Dictionary<string, TypeSymbol>> typesByModule)
	{
		var external = new Dictionary<string, TypeSymbol>(StringComparer.Ordinal);
		foreach (var use in unit.Declarations.OfType<UseDeclaration>().Where(ConditionalCompilation.IsIncluded))
		{
			if (use.Path.StartsWith("kern.", StringComparison.Ordinal))
				continue;
			if (use.Path.StartsWith("dotnet.", StringComparison.Ordinal))
				continue;
			if (!typesByModule.TryGetValue(use.Path, out var target))
				continue;

			if (use.IsWildcard)
			{
				foreach (var (name, symbol) in target)
					external.TryAdd(name, symbol);
				continue;
			}

			foreach (var item in use.ImportItems)
				if (target.TryGetValue(item, out var symbol))
					external.TryAdd(item, symbol);
		}

		return external;
	}

	private static ParsedFile? FindEntryModule(
		IReadOnlyList<ParsedFile> files,
		List<ProjectDiagnostic> diagnostics)
	{
		var withMain = files
			.Where(file => file.Module.Functions.TryGetValue("main", out var group) &&
				group.Any(function => function.Owner is null && !function.IsNative))
			.ToList();
		if (withMain.Count == 0)
			return null;

		if (withMain.Count == 1)
			return withMain[0];

		var root = withMain.FirstOrDefault(file =>
			Path.GetFileName(file.Path).Equals("main.kern", StringComparison.OrdinalIgnoreCase));
		if (root is not null && withMain.Count(item => !ReferenceEquals(item, root)) == 0)
			return root;

		foreach (var duplicate in withMain.Skip(1))
		{
			diagnostics.Add(new ProjectDiagnostic(
				duplicate.Path,
				new Diagnostic("KRN1012", "Функция main может быть только в одном модуле проекта", default)));
		}

		return root;
	}

	private static ModuleSymbol MergeSymbol(string projectName, IReadOnlyList<ParsedFile> files)
	{
		var types = new Dictionary<string, TypeSymbol>(StringComparer.Ordinal);
		var functions = new Dictionary<string, List<FunctionSymbol>>(StringComparer.Ordinal);
		var globals = new Dictionary<string, FieldSymbol>(StringComparer.Ordinal);
		var extensions = new Dictionary<string, ExtensionGroup>(StringComparer.Ordinal);
		var natives = new List<FunctionSymbol>();
		var seenTypes = new HashSet<TypeSymbol>(ReferenceEqualityComparer.Instance);
		var seenNatives = new HashSet<FunctionSymbol>(ReferenceEqualityComparer.Instance);

		foreach (var file in files)
		{
			foreach (var type in file.Module.Types.Values)
			{
				if (!seenTypes.Add(type))
					continue;
				types[$"{type.ModuleName ?? projectName}.{type.Name}"] = type;
			}

			foreach (var (name, group) in file.Module.Functions)
			{
				if (!functions.TryGetValue(name, out var merged))
					functions[name] = merged = [];
				foreach (var function in group)
					if (!function.IsNative && !merged.Contains(function))
						merged.Add(function);
			}

			foreach (var (name, field) in file.Module.Globals)
				globals.TryAdd(name, field);

			foreach (var (key, group) in file.Module.Extensions)
			{
				if (!extensions.TryGetValue(key, out var existing))
				{
					extensions[key] = group;
					continue;
				}

				extensions[key] = new ExtensionGroup(
					existing.ReceiverType,
					[.. existing.Functions, .. group.Functions],
					[.. existing.Properties, .. group.Properties]);
			}

			foreach (var native in file.Module.NativeFunctions)
				if (seenNatives.Add(native))
					natives.Add(native);
		}

		return new ModuleSymbol(
			projectName,
			types,
			functions.ToDictionary(
				pair => pair.Key,
				pair => (IReadOnlyList<FunctionSymbol>)pair.Value,
				StringComparer.Ordinal),
			globals,
			extensions)
		{
			NativeFunctions = natives
		};
	}

	private static void AddDiagnostics(
		List<ProjectDiagnostic> target,
		string path,
		IEnumerable<Diagnostic> diagnostics)
	{
		foreach (var diagnostic in diagnostics)
			target.Add(new ProjectDiagnostic(path, diagnostic));
	}

	private static bool HasErrors(IEnumerable<ProjectDiagnostic> diagnostics) =>
		diagnostics.Any(item => item.Diagnostic.IsError);

	private static string ModuleNameFromPath(string relativePath)
	{
		var withoutExtension = Path.ChangeExtension(relativePath, null) ?? relativePath;
		var normalized = withoutExtension.Replace('\\', '.').Replace('/', '.');
		// Пути проекта начинаются с корня источников (<dir>/src/...): src не входит в имя модуля.
		if (normalized.StartsWith("src.", StringComparison.Ordinal))
			normalized = normalized[4..];
		return normalized;
	}
}
