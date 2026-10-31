using Kern.Compiler.Semantics.Declare.Collect;
using Kern.Compiler.Semantics.Project;
using Kern.Compiler.Semantics.Scopes;
using Kern.Compiler.Semantics.Symbols;
using Kern.Core;

namespace Kern.Compiler.Semantics.Declare;

/// <summary>
/// Оркестратор «части 1» семантики. Фазы:
/// 1 скелеты типов и групп функций → 2 `use` и prelude → 3 заголовки типов (родители, ограничения)
/// → 4 члены и сигнатуры → 5 атрибуты. Тела функций не трогает (это часть 2).
/// </summary>
public sealed class DeclarationCollector(INativeSymbolSource natives, DiagnosticBag diagnostics)
{
	/// <summary>Модули, чьи публичные имена видны без `use`.</summary>
	public static readonly IReadOnlyList<string> PreludeModules =
		["kern.core", "kern.text", "kern.collections", "kern.numerics", "kern.math", "kern.io"];

	public ProjectModel Collect(string projectName, IReadOnlyList<SourceUnit> sources, string? rootDir = null, string? srcDir = null, string? entry = null)
	{
		var modules = new ModuleTable();
		var binder = new NativeBinder(natives);
		var resolver = new TypeResolver(modules, binder, diagnostics);
		var prelude = new Scope(null, "prelude");
		var ctx = new CollectContext(modules, binder, resolver, diagnostics, prelude);

		// Модули и области файлов.
		foreach (var src in sources)
		{
			var module = modules.GetOrAdd(src.ModuleName, src.IsStd);
			module.Scope ??= new Scope(prelude, $"module {module.Name}");
			module.Units.Add(src.Unit);
			var fileScope = new Scope(module.Scope, $"file {src.Unit.File.Path}");
			ctx.Files.Add(new FileModel(src.Unit, module, fileScope, src.IsStd));
		}

		var top = new TopLevelDeclarer(ctx);
		var imports = new ImportResolver(ctx);
		var headers = new HeaderResolver(ctx);
		var members = new MemberDeclarer(ctx);

		foreach (var f in ctx.Files) top.Declare(f);

		imports.PopulatePrelude(PreludeModules);
		foreach (var f in ctx.Files) imports.ResolveFile(f);

		headers.ResolveAll(ctx.Types);
		resolver.CheckConstraints = true;

		foreach (var f in ctx.Files) members.DeclareTopLevel(f);
		// Список может расти (вложенные типы) — копия на момент фазы.
		foreach (var t in ctx.Types.ToArray()) members.DeclareTypeMembers(t);

		new AttributeBinder(ctx).BindAll();

		return new ProjectModel
		{
			Name = projectName,
			RootDirectory = rootDir,
			SourceDirectory = srcDir,
			EntryFile = entry,
			Modules = modules,
			Files = ctx.Files,
			Diagnostics = diagnostics,
			Binder = binder,
			Resolver = resolver,
			Prelude = prelude,
			Types = ctx.Types,
		};
	}
}
