using Kern.Bytecode;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.CodeGen;

/// <summary>
/// M3-D: инициализаторы полей и автосвойств в прологе конструктора (spec §8, п.827–828).
/// Порядок пролога: инициализаторы в порядке объявления, затем копии <c>$</c>-параметров.
/// </summary>
public sealed class InitializerCodegenTests
{
	[Fact]
	public void Field_initializer_runs_before_constructor_body()
	{
		const string source =
			"""
			type Box {
				i32 value = 7
				ctor() { $.value = $.value + 1 }
			}

			fun f() -> i32 {
				Box box = Box()
				box.value
			}
			""";

		Assert.Equal(KValue.I32(8), Run(source, "f"));
	}

	[Fact]
	public void Auto_property_initializer_is_readable_through_the_property()
	{
		const string source =
			"""
			type Counter {
				i32 val { get; set } = 5
			}

			fun f() -> i32 {
				Counter counter = Counter()
				counter.val
			}
			""";

		Assert.Equal(KValue.I32(5), Run(source, "f"));
	}

	[Fact]
	public void Initializer_reads_a_primary_constructor_parameter()
	{
		const string source =
			"""
			type Origin(i32 x) {
				i32 doubled = x * 2
			}

			fun f() -> i32 {
				Origin origin = Origin(21)
				origin.doubled
			}
			""";

		Assert.Equal(KValue.I32(42), Run(source, "f"));
	}

	[Fact]
	public void Initializer_reads_a_primary_constructor_field_before_it_is_copied()
	{
		const string source =
			"""
			type Origin(i32 x) {
				i32 doubled = $.x * 2
			}

			fun f() -> i32 {
				Origin origin = Origin(21)
				origin.doubled
			}
			""";

		Assert.Equal(KValue.I32(42), Run(source, "f"));
	}

	[Fact]
	public void Initializers_run_in_declaration_order()
	{
		const string source =
			"""
			type Pair {
				i32 first = 5
				i32 second = $.first + 1
			}

			fun f() -> i32 {
				Pair pair = Pair()
				pair.second
			}
			""";

		Assert.Equal(KValue.I32(6), Run(source, "f"));
	}

	[Fact]
	public void Property_initializer_declared_before_a_field_keeps_declaration_order()
	{
		const string source =
			"""
			type Mixed {
				i32 val { get; set } = 5
				i32 following = $.val + 1
			}

			fun f() -> i32 {
				Mixed mixed = Mixed()
				mixed.following
			}
			""";

		Assert.Equal(KValue.I32(6), Run(source, "f"));
	}

	[Fact]
	public void Primary_constructor_unit_parameter_stays_an_independent_copy()
	{
		const string source =
			"""
			unit Point(i32 x)
			type Holder(Point p)

			fun f() -> i32 {
				Point point = Point(2)
				Holder holder = Holder(point)
				point.x = 9
				holder.p.x
			}
			""";

		Assert.Equal(KValue.I32(2), Run(source, "f"));
	}

	[Fact]
	public void Primary_constructor_field_store_copies_a_mutable_unit()
	{
		const string source =
			"""
			unit Point(i32 x)
			type Holder(Point p)

			fun f() -> i32 {
				Holder holder = Holder(Point(2))
				holder.p.x
			}
			""";

		var result = Compile(source);
		var type = Assert.Single(result.Module.TypePool, definition => definition.Name == "Holder");
		var constructor = result.Module.FunctionPool[Assert.Single(type.Ctors)];
		var code = constructor.Code;

		var copies = false;
		for (var index = 0; index + 1 < code.Length; index++)
			copies |= code[index] == (int)OpCode.CopyUnit && code[index + 1] == (int)OpCode.StoreField;

		Assert.True(copies, "Сохранение unit-параметра первичного ctor должно копировать значение (CopyUnit).");
	}

	[Fact]
	public void Default_constructor_runs_initializers()
	{
		const string source =
			"""
			type Marker {
				i32 value = 42
			}

			fun f() -> i32 {
				Marker marker = Marker()
				marker.value
			}
			""";

		Assert.Equal(KValue.I32(42), Run(source, "f"));
	}

	[Fact]
	public void Type_without_initializers_and_constructor_still_constructs()
	{
		const string source =
			"""
			type Empty {
				i32 value
			}

			fun f() -> i32 {
				Empty empty = Empty()
				1
			}
			""";

		Assert.Equal(KValue.I32(1), Run(source, "f"));
	}

	private static KValue Run(string source, string function, params KValue[] arguments)
	{
		var result = Compile(source);
		Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.IsError);
		return new Interpreter(result.Module).Run(function, arguments);
	}

	private static CodegenResult Compile(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		var bound = new Binder(module, table).Bind();
		Assert.DoesNotContain(bound.Diagnostics, diagnostic => diagnostic.IsError);
		return new CodeGenerator().Generate(bound);
	}
}
