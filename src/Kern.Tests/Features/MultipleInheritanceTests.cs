using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Native;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Множественное наследование (spec §2, срез A3a): два независимых родителя, выбор родителя
/// <c>$[A]member</c> для конфликтующих членов, вызов всех родительских конструкторов,
/// upcast/<c>is</c> к каждому родителю. Ромб пока диагностируется (<c>KRN2101</c>).
/// </summary>
public sealed class MultipleInheritanceTests
{
	[Fact]
	public void Parent_selectors_resolve_method_and_field()
	{
		const string source = """
			open type A {
				str name = "a"
				fun hello(i32 x) -> str f"A: ${x}"
			}

			open type B {
				str name = "b"
				fun hello(i32 x) -> str f"B: ${x}"
			}

			type C()[A, B] {
				fun $[A]hello(i32 x) -> str
				str $[A]name
			}

			fun hello() -> str {
				C c()
				c.hello(1)
			}

			fun name() -> str {
				C c()
				c.name
			}
			""";

		Assert.Equal(KValue.Str("A: 1"), Run(source, "hello"));
		Assert.Equal(KValue.Str("a"), Run(source, "name"));
	}

	[Fact]
	public void Both_parent_constructors_run_with_their_arguments()
	{
		const string source = """
			type P(str name) { }
			type Q(str tag) { }
			type R(str n, str t)[P(n), Q(t)] { }

			fun name() -> str {
				R r("n", "t")
				r.name
			}

			fun tag() -> str {
				R r("n", "t")
				r.tag
			}
			""";

		Assert.Equal(KValue.Str("n"), Run(source, "name"));
		Assert.Equal(KValue.Str("t"), Run(source, "tag"));
	}

	[Fact]
	public void Conflicting_member_without_selector_reports_KRN2103()
	{
		const string source = """
			open type A { str name = "a" }
			open type B { str name = "b" }
			type C()[A, B] { }

			fun main {
				C c()
				println(c.name)
			}
			""";

		var outcome = CompileWithDiagnostics(source);
		Assert.Contains(
			outcome.Diagnostics,
			diagnostic => diagnostic.Code == "KRN2103" &&
				diagnostic.Message.Contains("Неоднозначный член 'name'", StringComparison.Ordinal));
	}

	[Fact]
	public void Derived_upcasts_and_is_to_each_parent()
	{
		const string source = """
			open type A { str name = "a" }
			open type B { str name = "b" }
			type C()[A, B] {
				str $[A]name
			}

			fun as_a() -> str {
				C c()
				A a = c
				a.name
			}

			fun as_b() -> bool {
				C c()
				c is B
			}
			""";

		Assert.Equal(KValue.Str("a"), Run(source, "as_a"));
		Assert.Equal(KValue.Bool(true), Run(source, "as_b"));
	}

	[Fact]
	public void Diamond_shares_one_ancestor_and_runs_its_constructor_once()
	{
		const string source = """
			open type A {
				str name = "a"
				#i32 made = 0
				ctor() { A.made += 1 }
			}

			type B()[A] { }
			type C()[A, B] { }

			fun made() -> i32 {
				C c()
				A.made
			}

			fun name() -> str {
				C c()
				c.name
			}

			fun both() -> bool {
				C c()
				c is A && c is B
			}
			""";

		Assert.Equal(KValue.I32(1), Run(source, "made"));
		Assert.Equal(KValue.Str("a"), Run(source, "name"));
		Assert.Equal(KValue.Bool(true), Run(source, "both"));
	}

	[Fact]
	public void Diamond_selector_through_indirect_path_finds_the_shared_member()
	{
		const string source = """
			open type A { str name = "a" }
			type B1()[A] { }
			type B2()[A] { }
			type C()[B1, B2] { str $[B1]name }

			fun name() -> str {
				C c()
				c.name
			}

			fun is_a() -> bool {
				C c()
				c is A
			}
			""";

		Assert.Equal(KValue.Str("a"), Run(source, "name"));
		Assert.Equal(KValue.Bool(true), Run(source, "is_a"));
	}

	[Fact]
	public void Class_printing_skips_service_layout_slots()
	{
		const string source = """
			type A { i32 x = 1 }
			type B { i32 y = 2 }

			fun text() -> str {
				B b()
				b.to_str()
			}
			""";

		Assert.Equal(KValue.Str("B(y=2)"), Run(source, "text"));
	}

	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	private static KValue Run(string source, string function, params KValue[] arguments) =>
		new Interpreter(Compile(source).Module, new NativeResolver(Catalog())).Run(function, arguments);

	private static CodegenResult Compile(string source)
	{
		var outcome = CompileWithDiagnostics(source);
		Assert.DoesNotContain(outcome.Diagnostics, diagnostic => diagnostic.IsError);
		Assert.NotNull(outcome.Module);
		return outcome.Module!;
	}

	private sealed record Outcome(CodegenResult? Module, IReadOnlyList<Diagnostic> Diagnostics);

	/// <summary>Полный конвейер одного файла с сохранением диагностик.</summary>
	private static Outcome CompileWithDiagnostics(string source)
	{
		var diagnostics = new List<Diagnostic>();
		var parsed = Parser.Parse(source);
		diagnostics.AddRange(parsed.Diagnostics);
		if (HasErrors(diagnostics))
			return new Outcome(null, diagnostics);

		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		diagnostics.AddRange(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		diagnostics.AddRange(collectorDiagnostics);

		var catalog = Catalog();
		ModuleSymbol imported = NativeSymbolBuilder.Import(module, catalog, out var importProblems);
		Assert.Empty(importProblems);

		var bound = new Binder(imported, table).Bind();
		diagnostics.AddRange(bound.Diagnostics);
		if (HasErrors(diagnostics))
			return new Outcome(null, diagnostics);

		var generated = new CodeGenerator().Generate(bound);
		diagnostics.AddRange(generated.Diagnostics);
		return HasErrors(diagnostics)
			? new Outcome(null, diagnostics)
			: new Outcome(generated, diagnostics);
	}

	private static bool HasErrors(IReadOnlyList<Diagnostic> diagnostics) =>
		diagnostics.Any(diagnostic => diagnostic.IsError);
}
