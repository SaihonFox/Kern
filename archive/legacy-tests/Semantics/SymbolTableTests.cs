using Kern.Compiler.Semantics;
using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Semantics;

public sealed class SymbolTableTests
{
	[Fact]
	public void Collects_named_symbols_and_keeps_function_overloads_together()
	{
		var syntax = Parser.Parse("type Person {} unit Meter {} fun calculate(i32 x) {} fun calculate(str x) {} i32 count = 0");
		Assert.Empty(syntax.Diagnostics);

		var (table, diagnostics) = SymbolTable.Collect(syntax.Tree);

		Assert.Empty(diagnostics);
		Assert.Equal(SymbolKind.Type, Assert.Single(table.GetSymbols("Person")).Kind);
		Assert.Equal(SymbolKind.Unit, Assert.Single(table.GetSymbols("Meter")).Kind);
		Assert.Equal(2, table.GetSymbols("calculate").Count);
		Assert.Equal(SymbolKind.GlobalField, Assert.Single(table.GetSymbols("count")).Kind);
	}

	[Fact]
	public void Reports_duplicate_non_function_name_at_later_declaration()
	{
		var syntax = Parser.Parse("type Node {} type Node {}");
		Assert.Empty(syntax.Diagnostics);

		var (table, diagnostics) = SymbolTable.Collect(syntax.Tree);

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN1400", diagnostic.Code);
		Assert.Equal(syntax.Tree.Declarations[1].Span, diagnostic.Span);
		Assert.Single(table.GetSymbols("Node"));
	}

	[Fact]
	public void Generic_arity_is_part_of_function_signature()
	{
		var syntax = Parser.Parse("fun map<T>(T value) {} fun map<T, U>(T value) {}");
		Assert.Empty(syntax.Diagnostics);

		var (table, diagnostics) = SymbolTable.Collect(syntax.Tree);

		Assert.Empty(diagnostics);
		Assert.Equal(2, table.GetSymbols("map").Count);
		Assert.Equal(new[] { 1, 2 }, table.GetSymbols("map")
			.Select(symbol => ((FunctionDeclaration)symbol.Declaration).GenericArity));
	}

	[Fact]
	public void Generic_parameter_type_is_part_of_function_signature()
	{
		var syntax = Parser.Parse("fun pick<T>(i32 value) {} fun pick<T>(str value) {}");
		Assert.Empty(syntax.Diagnostics);

		var (table, diagnostics) = SymbolTable.Collect(syntax.Tree);

		Assert.Empty(diagnostics);
		Assert.Equal(2, table.GetSymbols("pick").Count);
	}

	[Fact]
	public void Generic_parameter_names_are_part_of_function_signature()
	{
		var syntax = Parser.Parse("fun map<T>(i32 value) {} fun map<U>(i32 value) {}");
		Assert.Empty(syntax.Diagnostics);

		var (table, diagnostics) = SymbolTable.Collect(syntax.Tree);

		Assert.Empty(diagnostics);
		Assert.Equal(2, table.GetSymbols("map").Count);
	}

	[Fact]
	public void Generic_type_declaration_keeps_its_arity()
	{
		var syntax = Parser.Parse("type Pair<T, U> {}");
		Assert.Empty(syntax.Diagnostics);

		var type = Assert.IsType<TypeDeclaration>(Assert.Single(syntax.Tree.Declarations));
		Assert.Equal(2, type.GenericArity);
	}

	[Fact]
	public void Reports_duplicate_function_signature()
	{
		var syntax = Parser.Parse("fun run(i32 value) {} fun run(i32 other) {}");
		Assert.Empty(syntax.Diagnostics);

		var (table, diagnostics) = SymbolTable.Collect(syntax.Tree);

		Assert.Equal("KRN1400", Assert.Single(diagnostics).Code);
		Assert.Single(table.GetSymbols("run"));
	}

	[Fact]
	public void Reports_non_function_collision_with_function_name()
	{
		var syntax = Parser.Parse("type Thing {} fun Thing() {}");
		Assert.Empty(syntax.Diagnostics);

		var (_, diagnostics) = SymbolTable.Collect(syntax.Tree);

		Assert.Equal("KRN1400", Assert.Single(diagnostics).Code);
	}
}
