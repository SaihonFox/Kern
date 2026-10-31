using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Modules;

/// <summary>Диагностика, привязанная к модулю (файлу): путь добавляет CLI.</summary>
public sealed record ModuleDiagnostic(string ModuleName, Diagnostic Diagnostic);

/// <summary>
/// Область видимости модуля: собственные имена плюс импортированные через <c>use</c>
/// (с учётом re-export). Собственное объявление всегда важнее импорта; конфликт двух
/// импортов диагностируется при разрешении модулей.
/// </summary>
public sealed class ModuleScope
{
	private readonly IReadOnlyDictionary<string, TypeSymbol> _types;
	private readonly IReadOnlyDictionary<string, IReadOnlyList<FunctionSymbol>> _functions;
	private readonly IReadOnlyDictionary<string, FieldSymbol> _globals;
	private readonly IReadOnlyDictionary<string, ExtensionGroup> _extensions;

	private ModuleScope(
		ModuleSymbol module,
		IReadOnlyDictionary<string, TypeSymbol> types,
		IReadOnlyDictionary<string, IReadOnlyList<FunctionSymbol>> functions,
		IReadOnlyDictionary<string, FieldSymbol> globals,
		IReadOnlyDictionary<string, ExtensionGroup> extensions)
	{
		Module = module;
		_types = types;
		_functions = functions;
		_globals = globals;
		_extensions = extensions;
	}

	public ModuleSymbol Module { get; }

	/// <summary>Модуль без импортов (старый путь и unit-тесты).</summary>
	public static ModuleScope ForModule(ModuleSymbol module) =>
		new(module, module.Types, module.Functions, module.Globals, module.Extensions);

	internal static ModuleScope Create(
		ModuleSymbol module,
		IReadOnlyDictionary<string, TypeSymbol> types,
		IReadOnlyDictionary<string, IReadOnlyList<FunctionSymbol>> functions,
		IReadOnlyDictionary<string, FieldSymbol> globals,
		IReadOnlyDictionary<string, ExtensionGroup> extensions) =>
		new(module, types, functions, globals, extensions);

	public TypeSymbol? FindType(string name) =>
		_types.TryGetValue(name, out var type) ? type : null;

	public IReadOnlyList<FunctionSymbol> GetFunctions(string name) =>
		_functions.TryGetValue(name, out var group) ? group : [];

	public FieldSymbol? FindGlobal(string name) =>
		_globals.TryGetValue(name, out var field) ? field : null;

	public ExtensionGroup? FindExtensions(KernType receiverType) =>
		_extensions.TryGetValue(receiverType.ToString(), out var group) ? group : null;
}

/// <summary>
/// Разрешение <c>use</c> между модулями проекта (M3-F): проверка существования модуля и
/// имён, склейка областей видимости, re-export (транзитивно) и диагностика конфликтов.
/// Нативные модули <c>kern.*</c> считаются доступными: их имена уже глобальны.
/// </summary>
public static class ModuleResolver
{
	public static (IReadOnlyDictionary<string, ModuleScope> Scopes, IReadOnlyList<ModuleDiagnostic> Diagnostics)
		Resolve(IReadOnlyList<ModuleSymbol> modules)
	{
		var diagnostics = new List<ModuleDiagnostic>();
		var byName = new Dictionary<string, ModuleSymbol>(StringComparer.Ordinal);
		foreach (var module in modules)
		{
			if (!byName.TryAdd(module.Name, module))
			{
				diagnostics.Add(new ModuleDiagnostic(
					module.Name,
					new Diagnostic("KRN1010", $"Модуль '{module.Name}' объявлен несколько раз", default)));
			}
		}

		var exports = new Dictionary<string, ExportSet>(StringComparer.Ordinal);
		var scopes = new Dictionary<string, ModuleScope>(StringComparer.Ordinal);
		foreach (var module in byName.Values)
			BuildExports(module, byName, exports, diagnostics);

		foreach (var module in byName.Values)
		{
			var export = exports[module.Name];
			scopes[module.Name] = ModuleScope.Create(
				module,
				export.Types,
				export.Functions.ToDictionary(
					pair => pair.Key,
					pair => (IReadOnlyList<FunctionSymbol>)pair.Value,
					StringComparer.Ordinal),
				export.Globals,
				export.Extensions);
		}

		return (scopes, diagnostics);
	}

	private sealed class ExportSet
	{
		public Dictionary<string, TypeSymbol> Types { get; } = new(StringComparer.Ordinal);
		public Dictionary<string, List<FunctionSymbol>> Functions { get; } = new(StringComparer.Ordinal);
		public Dictionary<string, FieldSymbol> Globals { get; } = new(StringComparer.Ordinal);
		public Dictionary<string, ExtensionGroup> Extensions { get; } = new(StringComparer.Ordinal);
		public HashSet<string> ImportedTypes { get; } = new(StringComparer.Ordinal);
		public HashSet<string> ImportedFunctions { get; } = new(StringComparer.Ordinal);
		public HashSet<string> ImportedGlobals { get; } = new(StringComparer.Ordinal);
	}

	private static ExportSet BuildExports(
		ModuleSymbol module,
		IReadOnlyDictionary<string, ModuleSymbol> byName,
		Dictionary<string, ExportSet> exports,
		List<ModuleDiagnostic> diagnostics)
	{
		if (exports.TryGetValue(module.Name, out var existing))
			return existing;

		var result = new ExportSet();
		foreach (var (name, type) in module.Types)
			if (type.Kind != Kern.Compiler.Semantics.Types.SymbolKind.Native)
				result.Types[name] = type;
		foreach (var (name, group) in module.Functions)
			result.Functions[name] = [.. group.Where(function => !function.IsNative)];
		foreach (var (name, field) in module.Globals)
			result.Globals[name] = field;
		foreach (var (key, group) in module.Extensions)
			result.Extensions[key] = group;

		// Циклические импорты: пока модуль строится, используем его собственные имена.
		exports[module.Name] = result;

		foreach (var use in module.Uses)
		{
			if (use.ImportItems.Count == 0 && !use.IsWildcard)
			{
				diagnostics.Add(new ModuleDiagnostic(
					module.Name,
					new Diagnostic(
						"KRN1009",
						"use требует список имён [..] или .*; импорт модуля целиком — use path.module.*",
						use.Span)));
				continue;
			}

			if (use.Path.StartsWith("kern.", StringComparison.Ordinal))
				continue;

			if (!byName.TryGetValue(use.Path, out var target))
			{
				diagnostics.Add(new ModuleDiagnostic(
					module.Name,
					new Diagnostic("KRN1010", $"Модуль '{use.Path}' не найден", use.Span)));
				continue;
			}

			var targetExport = BuildExports(target, byName, exports, diagnostics);
			Import(target, targetExport, use, result, diagnostics, module.Name);
		}

		return result;
	}

	private static void Import(
		ModuleSymbol target,
		ExportSet source,
		UseDeclaration use,
		ExportSet destination,
		List<ModuleDiagnostic> diagnostics,
		string importer)
	{
		if (use.IsWildcard)
		{
			ImportAll(target, source, destination, diagnostics, importer, use.Span);
			return;
		}

		foreach (var item in use.ImportItems)
		{
			var found = false;
			if (source.Types.TryGetValue(item, out var type))
			{
				AddType(destination, item, type, target.Name, diagnostics, importer, use.Span);
				found = true;
			}

			if (source.Functions.TryGetValue(item, out var functions))
			{
				AddFunctions(destination, item, functions, target.Name, diagnostics, importer, use.Span);
				found = true;
			}

			if (source.Globals.TryGetValue(item, out var field))
			{
				AddGlobal(destination, item, field, target.Name, diagnostics, importer, use.Span);
				found = true;
			}

			// Имя может быть членом расширения: расширения импортируются модулем целиком
			// (см. ниже), поэтому достаточно признать имя существующим.
			if (!found && source.Extensions.Values.Any(group =>
				group.Functions.Any(function => function.Name == item) ||
				group.Properties.Any(property => property.Name == item)))
			{
				found = true;
			}

			if (!found)
			{
				diagnostics.Add(new ModuleDiagnostic(
					importer,
					new Diagnostic("KRN1011", $"Модуль '{target.Name}' не экспортирует '{item}'", use.Span)));
			}
		}

		// Расширения импортируются вместе с модулем: методы/свойства чужих типов доступны.
		ImportExtensions(source, destination);
	}

	private static void ImportAll(
		ModuleSymbol target,
		ExportSet source,
		ExportSet destination,
		List<ModuleDiagnostic> diagnostics,
		string importer,
		TextSpan span)
	{
		foreach (var (name, type) in source.Types)
			AddType(destination, name, type, target.Name, diagnostics, importer, span);
		foreach (var (name, functions) in source.Functions)
			AddFunctions(destination, name, functions, target.Name, diagnostics, importer, span);
		foreach (var (name, field) in source.Globals)
			AddGlobal(destination, name, field, target.Name, diagnostics, importer, span);
		ImportExtensions(source, destination);
	}

	private static void AddType(
		ExportSet destination,
		string name,
		TypeSymbol type,
		string origin,
		List<ModuleDiagnostic> diagnostics,
		string importer,
		TextSpan span)
	{
		if (destination.Types.ContainsKey(name))
		{
			if (destination.ImportedTypes.Contains(name))
				ReportCollision(destination, name, origin, diagnostics, importer, span);
			return;
		}

		destination.Types[name] = type;
		destination.ImportedTypes.Add(name);
	}

	private static void AddFunctions(
		ExportSet destination,
		string name,
		IReadOnlyList<FunctionSymbol> functions,
		string origin,
		List<ModuleDiagnostic> diagnostics,
		string importer,
		TextSpan span)
	{
		if (destination.Functions.ContainsKey(name))
		{
			if (destination.ImportedFunctions.Contains(name))
				ReportCollision(destination, name, origin, diagnostics, importer, span);
			return;
		}

		destination.Functions[name] = [.. functions];
		destination.ImportedFunctions.Add(name);
	}

	private static void AddGlobal(
		ExportSet destination,
		string name,
		FieldSymbol field,
		string origin,
		List<ModuleDiagnostic> diagnostics,
		string importer,
		TextSpan span)
	{
		if (destination.Globals.ContainsKey(name))
		{
			if (destination.ImportedGlobals.Contains(name))
				ReportCollision(destination, name, origin, diagnostics, importer, span);
			return;
		}

		destination.Globals[name] = field;
		destination.ImportedGlobals.Add(name);
	}

	private static void ReportCollision(
		ExportSet destination,
		string name,
		string origin,
		List<ModuleDiagnostic> diagnostics,
		string importer,
		TextSpan span)
	{
		diagnostics.Add(new ModuleDiagnostic(
			importer,
			new Diagnostic(
				"KRN1011",
				$"Имя '{name}' импортируется из нескольких модулей (последний — '{origin}')",
				span)));
	}

	private static void ImportExtensions(ExportSet source, ExportSet destination)
	{
		foreach (var (key, group) in source.Extensions)
		{
			if (!destination.Extensions.TryGetValue(key, out var existing))
			{
				destination.Extensions[key] = group;
				continue;
			}

			destination.Extensions[key] = new ExtensionGroup(
				existing.ReceiverType,
				[.. existing.Functions, .. group.Functions],
				[.. existing.Properties, .. group.Properties]);
		}
	}
}
