using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Xunit;

namespace Kern.Tests.Binding;

/// <summary>
/// M3-D4, Q13: цель <c>a.b.c = v</c> привязывается как одна цепь записи —
/// корень-место и звенья, а не как вложенное чтение поля. Ошибки: <c>unit!</c>
/// в пути, get-only/<c>init</c>-звено и временное unit-значение.
/// </summary>
public sealed class NestedStoreBindingTests
{
	private const string UnitTypes = """
		unit Inner(i32 x)
		unit Outer(Inner inner)
		type Box {
			Inner inner
		}
		""";

	[Fact]
	public void Nested_field_store_binds_as_one_chain()
	{
		var module = Bind(UnitTypes + """

			fun f(Box o) -> i32 {
				o.inner.x = 5
				o.inner.x
			}
			""");

		var chain = Chain(module, "f");
		var root = Assert.IsType<BoundLocalStoreRoot>(chain.Root);
		Assert.Equal("o", root.Name);
		Assert.Equal("Box", Assert.IsType<ClassType>(root.Type).Symbol.Name);

		Assert.Collection(
			chain.Links,
			link =>
			{
				var field = Assert.IsType<BoundFieldStoreLink>(link);
				Assert.Equal("inner", field.Field.Name);
				Assert.Equal("Inner", Assert.IsType<UnitType>(field.Type).Symbol.Name);
			},
			link =>
			{
				var field = Assert.IsType<BoundFieldStoreLink>(link);
				Assert.Equal("x", field.Field.Name);
				Assert.Equal(PrimitiveType.I32, field.Type);
			});
	}

	[Fact]
	public void Unit_local_root_is_a_place_and_writes_back()
	{
		var module = Bind(UnitTypes + """

			fun f() -> i32 {
				Outer o = Outer(Inner(1))
				o.inner.x = 5
				o.inner.x
			}
			""");

		var chain = Chain(module, "f");
		var root = Assert.IsType<BoundLocalStoreRoot>(chain.Root);
		Assert.Equal("Outer", Assert.IsType<UnitType>(root.Type).Symbol.Name);
		Assert.Equal(2, chain.Links.Count);
	}

	[Fact]
	public void Static_root_chain_starts_with_the_static_place()
	{
		var module = Bind("""
			unit Inner(i32 x)
			type Holder {
				#Inner cache
			}

			fun f() -> i32 {
				Holder.cache.x = 5
				Holder.cache.x
			}
			""");

		var chain = Chain(module, "f");
		var root = Assert.IsType<BoundStaticFieldStoreRoot>(chain.Root);
		Assert.Equal("cache", root.Field.Name);
		Assert.True(root.Field.IsStatic);
		var link = Assert.Single(chain.Links);
		Assert.Equal("x", Assert.IsType<BoundFieldStoreLink>(link).Field.Name);
	}

	[Fact]
	public void Computed_property_link_keeps_getter_and_setter()
	{
		var module = Bind("""
			unit Inner(i32 x)
			type Box {
				Inner inner {
					get Inner(1)
					set(v) { }
				}
			}

			fun f(Box o) -> i32 {
				o.inner.x = 5
				o.inner.x
			}
			""");

		var chain = Chain(module, "f");
		var property = Assert.IsType<BoundPropertyStoreLink>(chain.Links[0]);
		Assert.Equal("inner", property.Property.Name);
		Assert.NotNull(property.Getter);
		Assert.NotNull(property.Writer);
		Assert.Equal("x", Assert.IsType<BoundFieldStoreLink>(chain.Links[1]).Field.Name);
	}

	[Fact]
	public void Get_only_property_link_reports_KRN2105()
	{
		var diagnostic = Assert.Single(Diagnostics("""
			unit Inner(i32 x)
			type Box {
				Inner inner { get; }
			}

			fun f(Box o) {
				o.inner.x = 5
			}
			"""));

		Assert.Equal("KRN2105", diagnostic.Code);
		Assert.Contains("inner", diagnostic.Message);
		Assert.Contains("только для чтения", diagnostic.Message);
	}

	[Fact]
	public void Init_only_property_link_outside_construction_reports_KRN2105()
	{
		var diagnostic = Assert.Single(Diagnostics("""
			unit Inner(i32 x)
			type Box {
				Inner inner { get; init }
			}

			fun f(Box o) {
				o.inner.x = 5
			}
			"""));

		Assert.Equal("KRN2105", diagnostic.Code);
		Assert.Contains("inner", diagnostic.Message);
		Assert.Contains("при создании объекта", diagnostic.Message);
	}

	[Fact]
	public void Immutable_unit_link_reports_KRN2105()
	{
		var diagnostic = Assert.Single(Diagnostics("""
			unit! Frozen(i32 x)
			type Box {
				Frozen frozen
			}

			fun f(Box o) {
				o.frozen.x = 5
			}
			"""));

		Assert.Equal("KRN2105", diagnostic.Code);
		Assert.Contains("unit!", diagnostic.Message);
	}

	[Fact]
	public void Immutable_unit_root_reports_KRN2105()
	{
		var diagnostic = Assert.Single(Diagnostics("""
			unit! Frozen(i32 x)
			fun f(Frozen p) {
				p.x = 5
			}
			"""));

		Assert.Equal("KRN2105", diagnostic.Code);
		Assert.Contains("unit!", diagnostic.Message);
	}

	[Fact]
	public void Temporary_unit_root_reports_KRN2106()
	{
		var diagnostic = Assert.Single(Diagnostics("""
			unit Point(i32 x)
			fun f() {
				Point(0).x = 5
			}
			"""));

		Assert.Equal("KRN2106", diagnostic.Code);
		Assert.Contains("временного unit", diagnostic.Message);
	}

	[Fact]
	public void Temporary_unit_chain_root_reports_KRN2106()
	{
		var diagnostic = Assert.Single(Diagnostics("""
			unit Inner(i32 x)
			unit Outer(Inner inner)
			fun make() -> Outer { Outer(Inner(1)) }
			fun f() {
				make().inner.x = 5
			}
			"""));

		Assert.Equal("KRN2106", diagnostic.Code);
		Assert.Contains("временное unit", diagnostic.Message);
	}

	[Fact]
	public void Required_static_root_reports_KRN2105()
	{
		var diagnostic = Assert.Single(Diagnostics("""
			unit Inner(i32 x)
			type Holder {
				#Inner! cache
			}

			fun f() {
				Holder.cache.x = 5
			}
			"""));

		Assert.Equal("KRN2105", diagnostic.Code);
		Assert.Contains("Статическая константа", diagnostic.Message);
	}

	private static BoundChainStore Chain(BoundModule module, string function)
	{
		var body = module.FindFunction(function)!.Body;
		return Assert.Single(body.Statements.OfType<BoundChainStore>());
	}

	private static BoundModule Bind(string source)
	{
		var (module, diagnostics) = BindWithDiagnostics(source);
		Assert.Empty(diagnostics);
		return module;
	}

	private static IReadOnlyList<Diagnostic> Diagnostics(string source) =>
		BindWithDiagnostics(source).Diagnostics;

	private static (BoundModule Module, IReadOnlyList<Diagnostic> Diagnostics) BindWithDiagnostics(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		var bound = new Binder(module, table).Bind();
		return (bound, bound.Diagnostics);
	}
}
