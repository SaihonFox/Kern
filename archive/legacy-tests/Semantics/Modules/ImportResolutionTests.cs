using Kern.Compiler.Semantics.Modules;
using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Semantics.Modules;

public sealed class ImportResolutionTests
{
	[Fact]
	public void Parser_retains_selected_import_items()
	{
		var result = Parser.Parse("use geometry.shapes[Circle, Rect]");

		Assert.Empty(result.Diagnostics);
		var declaration = Assert.IsType<UseDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.Equal("geometry.shapes", declaration.Path);
		Assert.Equal(["Circle", "Rect"], declaration.ImportItems);
		Assert.False(declaration.IsWildcard);
	}

	[Fact]
	public void Parser_retains_wildcard_without_trailing_dot()
	{
		var result = Parser.Parse("use geometry.format.*");

		Assert.Empty(result.Diagnostics);
		var declaration = Assert.IsType<UseDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.Equal("geometry.format", declaration.Path);
		Assert.Empty(declaration.ImportItems);
		Assert.True(declaration.IsWildcard);
	}

	[Fact]
	public void Selected_import_is_resolved_and_exposed()
	{
		var graph = Resolve(
			"use geometry.shapes[Circle, Rect]",
			("geometry.shapes", "type Circle {} type Rect {} type Other {}"));

		Assert.Empty(graph.Diagnostics);
		var import = Assert.Single(graph.ImportsOf("main"));
		Assert.Equal("geometry.shapes", import.Module.Name);
		Assert.Equal(["Circle", "Rect"], import.Items);
		Assert.False(import.IsWildcard);
		Assert.False(import.ImportsEverything);
	}

	[Fact]
	public void Wildcard_import_checks_only_the_module()
	{
		var graph = Resolve(
			"use geometry.*",
			("geometry", "type Circle {} fun area(i32 x) -> i32 { x }"));

		Assert.Empty(graph.Diagnostics);
		var import = Assert.Single(graph.ImportsOf("main"));
		Assert.True(import.IsWildcard);
		Assert.True(import.ImportsEverything);
	}

	[Fact]
	public void Plain_module_import_is_recorded_without_item_checks()
	{
		var graph = Resolve(
			"use geometry",
			("geometry", "type Circle {}"));

		Assert.Empty(graph.Diagnostics);
		var import = Assert.Single(graph.ImportsOf("main"));
		Assert.Empty(import.Items);
		Assert.False(import.IsWildcard);
		Assert.True(import.ImportsEverything);
	}

	[Fact]
	public void Missing_module_reports_KRN1101()
	{
		var graph = Resolve("use geometry.shapes[Circle]");

		var diagnostic = Assert.Single(graph.Diagnostics);
		Assert.Equal("KRN1101", diagnostic.Code);
		Assert.Contains("geometry.shapes", diagnostic.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Missing_imported_symbol_reports_KRN1102()
	{
		var graph = Resolve(
			"use geometry.shapes[Circle, Square]",
			("geometry.shapes", "type Circle {}"));

		var diagnostic = Assert.Single(graph.Diagnostics);
		Assert.Equal("KRN1102", diagnostic.Code);
		Assert.Contains("Square", diagnostic.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Functions_units_and_globals_can_be_imported()
	{
		var graph = Resolve(
			"use helpers[area, Meter, limit]",
			("helpers", "fun area(i32 x) -> i32 { x } unit Meter {} i32 limit = 1"));

		Assert.Empty(graph.Diagnostics);
		Assert.Equal(3, Assert.Single(graph.ImportsOf("main")).Items.Count);
	}

	[Fact]
	public void transitive_imports_are_loaded_through_the_callback()
	{
		var graph = Resolve(
			"use a[Alpha]",
			("a", "use b[Beta] type Alpha {}"),
			("b", "type Beta {}"));

		Assert.Empty(graph.Diagnostics);
		Assert.True(graph.Modules.ContainsKey("a"));
		Assert.True(graph.Modules.ContainsKey("b"));
		Assert.Equal("b", Assert.Single(graph.ImportsOf("a")).Module.Name);
	}

	[Fact]
	public void Cyclic_imports_terminate()
	{
		var graph = Resolve(
			"use a[Alpha]",
			("a", "use b[Beta] type Alpha {}"),
			("b", "use a[Alpha] type Beta {}"));

		Assert.Empty(graph.Diagnostics);
		Assert.True(graph.Modules.ContainsKey("a"));
		Assert.True(graph.Modules.ContainsKey("b"));
		Assert.Equal(3, graph.Modules.Count);
	}

	private static ModuleGraph Resolve(string mainSource, params (string Name, string Source)[] modules)
	{
		var units = new Dictionary<string, CompilationUnit>(StringComparer.Ordinal);
		foreach (var (name, source) in modules)
			units.Add(name, Parse(source));

		var loader = new ModuleLoader(name => units.TryGetValue(name, out var unit) ? unit : null);
		return loader.Resolve([("main", Parse(mainSource))]);
	}

	private static CompilationUnit Parse(string source)
	{
		var result = Parser.Parse(source);
		Assert.Empty(result.Diagnostics);
		return result.Tree;
	}
}
