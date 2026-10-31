using Kern.Compiler.Semantics.Declarations;
using Kern.Core.Diagnostics;
using Xunit;

namespace Kern.Tests.Semantics.Declarations;

public sealed class DeclarationDiagnosticsTests
{
	[Fact]
	public void Reports_duplicate_function_signature_within_a_type()
	{
		var diagnostics = DeclarationTestSupport.Diagnostics(
			"type Calc { fun run(i32 first) {} fun run(i32 second) {} }");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN1400", diagnostic.Code);
		Assert.Equal("Повторная сигнатура функции 'run'", diagnostic.Message);
	}

	[Fact]
	public void Duplicate_signature_keeps_only_the_first_overload()
	{
		var module = DeclarationTestSupport.Collect(
			"type Calc { fun run(i32 first) {} fun run(i32 second) {} }").Module;

		Assert.Single(module.FindType("Calc")!.Members.GetFunctions("run"));
	}

	[Fact]
	public void Generic_parameter_names_are_not_part_of_overload_identity()
	{
		var diagnostics = DeclarationTestSupport.Diagnostics(
			"type Calc { fun map<T>(T value) -> T value fun map<U>(U other) -> U other }");

		Assert.Equal("KRN1400", Assert.Single(diagnostics).Code);
	}

	[Fact]
	public void Default_values_are_not_part_of_overload_identity()
	{
		var diagnostics = DeclarationTestSupport.Diagnostics(
			"type Calc { fun greet(str name = \"Guest\") {} fun greet(str other = \"X\") {} }");

		Assert.Equal("KRN1400", Assert.Single(diagnostics).Code);
	}

	[Fact]
	public void Generic_parameter_arity_is_part_of_overload_identity()
	{
		var module = DeclarationTestSupport.Module(
			"type Calc { fun map<T>(i32 value) {} fun map<T, U>(i32 value) {} }");

		var overloads = module.FindType("Calc")!.Members.GetFunctions("map");

		Assert.Equal(["map<1>(i32)", "map<2>(i32)"], overloads.Select(function => function.Signature));
	}

	[Fact]
	public void Reports_duplicate_field_name()
	{
		var diagnostics = DeclarationTestSupport.Diagnostics("type User { i32 age  str age }");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN1407", diagnostic.Code);
		Assert.Equal("Повторное имя члена 'age' в типе 'User'", diagnostic.Message);
	}

	[Fact]
	public void Reports_field_and_property_name_collision()
	{
		var diagnostics = DeclarationTestSupport.Diagnostics("type User { i32 age  i32 age { get; set } }");

		Assert.Equal("KRN1407", Assert.Single(diagnostics).Code);
	}

	[Fact]
	public void Reports_field_and_function_name_collision()
	{
		var diagnostics = DeclarationTestSupport.Diagnostics("type User { i32 age  fun age() {} }");

		Assert.Equal("KRN1407", Assert.Single(diagnostics).Code);
	}

	[Fact]
	public void Function_overloads_alone_do_not_conflict()
	{
		var diagnostics = DeclarationTestSupport.Diagnostics(
			"type User { fun age(i32 x) {} fun age(str x) {} fun age(i32 x, i32 y) {} }");

		Assert.Empty(diagnostics);
	}

	[Fact]
	public void Resolver_errors_do_not_stop_member_collection()
	{
		var (module, diagnostics) = DeclarationTestSupport.Collect(
			"type Broken { Unknown value  i32 x  i32 x  fun run(i32 a) {} fun run(i32 b) {} }");

		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN1001");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN1407");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN1400");
		Assert.All(diagnostics, diagnostic => Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity));

		var broken = module.FindType("Broken");
		Assert.NotNull(broken);
		Assert.Single(broken!.Members.GetFunctions("run"));
		Assert.Equal(2, broken.Members.Fields.Count);
	}

	[Fact]
	public void Unknown_member_type_is_reported_once_per_field()
	{
		var diagnostics = DeclarationTestSupport.Diagnostics("type Broken { Unknown first  Unknown second }");

		Assert.Equal(2, diagnostics.Count(diagnostic => diagnostic.Code == "KRN1001"));
	}

	[Fact]
	public void Duplicate_top_level_type_is_left_to_the_symbol_table()
	{
		// Коллектор не дублирует KRN1400 таблицы символов для top-level объявлений.
		var parsed = Kern.Compiler.Syntax.Parser.Parse("type Node {} type Node {}");
		var (table, tableDiagnostics) = Kern.Compiler.Semantics.SymbolTable.Collect(parsed.Tree);
		var (module, diagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "test");

		Assert.Single(tableDiagnostics);
		Assert.Empty(diagnostics);
		Assert.Single(module.Types);
	}
}
