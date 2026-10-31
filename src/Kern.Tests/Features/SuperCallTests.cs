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
/// Выбор родителя в выражении (spec §2): <c>$[A].member</c> — супер-вызов в стиле
/// <c>base.</c>. Член ищется в предке <c>A</c> и вызывается статически: виртуальный
/// диспатч по runtime-типу получателя отключён, поэтому переопределение наследника
/// не перехватывает вызов.
/// </summary>
public sealed class SuperCallTests
{
	[Fact]
	public void Override_calls_the_parent_implementation()
	{
		const string source = """
			open type A {
				open fun greet() -> str "A"
			}

			type B()[A] {
				~fun greet() -> str f"B(${$[A].greet()})"
			}

			fun go() -> str {
				B b()
				b.greet()
			}
			""";
		Assert.Equal(KValue.Str("B(A)"), Run(source, "go"));
	}

	[Fact]
	public void Three_level_chain_calls_each_parent_layer()
	{
		const string source = """
			open type A {
				open fun greet() -> str "A"
			}

			open type B()[A] {
				~fun greet() -> str f"B(${$[A].greet()})"
			}

			type C()[B] {
				~fun greet() -> str f"C(${$[B].greet()})"
			}

			fun go() -> str {
				C c()
				c.greet()
			}
			""";
		Assert.Equal(KValue.Str("C(B(A))"), Run(source, "go"));
	}

	[Fact]
	public void Super_field_read_ignores_shadowing()
	{
		const string source = """
			open type A {
				str name = "a"
			}

			type B()[A] {
				str name = "b"
				fun parent_name() -> str $[A].name
				fun own_name() -> str name
			}

			fun parent_field() -> str {
				B b()
				b.parent_name()
			}

			fun shadowing_field() -> str {
				B b()
				b.own_name()
			}
			""";
		Assert.Equal(KValue.Str("a"), Run(source, "parent_field"));
		Assert.Equal(KValue.Str("b"), Run(source, "shadowing_field"));
	}

	[Fact]
	public void Super_property_calls_the_parent_accessor()
	{
		const string source = """
			open type A {
				i32 v => 10
			}

			type B()[A] {
				i32 v => 20
				fun parent_value() -> i32 $[A].v
			}

			fun parent_property() -> i32 {
				B b()
				b.parent_value()
			}

			fun child_property() -> i32 {
				B b()
				b.v
			}

			fun virtual_property() -> i32 {
				A a = B()
				a.v
			}
			""";
		Assert.Equal(KValue.I32(10), Run(source, "parent_property"));
		Assert.Equal(KValue.I32(20), Run(source, "child_property"));
		// Обычный доступ через переменную типа A по-прежнему виртуален.
		Assert.Equal(KValue.I32(20), Run(source, "virtual_property"));
	}

	[Fact]
	public void Super_call_through_a_diamond_branch_uses_the_named_parent()
	{
		const string source = """
			open type A {
				fun foo() -> str "A"
			}

			open type B {
				fun foo() -> str "B"
			}

			type C()[A, B] {
				fun direct_a() -> str $[A].foo()
				fun direct_b() -> str $[B].foo()
			}

			fun go_a() -> str {
				C c()
				c.direct_a()
			}

			fun go_b() -> str {
				C c()
				c.direct_b()
			}
			""";
		Assert.Equal(KValue.Str("A"), Run(source, "go_a"));
		Assert.Equal(KValue.Str("B"), Run(source, "go_b"));
	}

	[Fact]
	public void Super_call_works_inside_interpolation()
	{
		const string source = """
			open type A {
				open fun greet() -> str "A"
			}

			type B()[A] {
				~fun greet() -> str f"B(${$[A].greet()})"
				fun plain() -> str f"${$[A].greet()}"
			}

			fun plain_interpolation() -> str {
				B b()
				b.plain()
			}
			""";
		Assert.Equal(KValue.Str("A"), Run(source, "plain_interpolation"));
	}

	[Fact]
	public void Unknown_parent_outside_instance_and_bare_selector_report_errors()
	{
		var unknown = CompileWithDiagnostics("""
			open type A {
				fun greet() -> str "A"
			}

			type B()[A] {
				fun call() -> str $[Missing].greet()
			}

			fun main { }
			""");
		var unknownError = Assert.Single(unknown.Diagnostics, diagnostic => diagnostic.Code == "KRN2103");
		Assert.Contains("Missing", unknownError.Message, StringComparison.Ordinal);
		Assert.Null(unknown.Code);

		var outside = CompileWithDiagnostics("""
			open type A {
				fun greet() -> str "A"
			}

			fun free() -> str $[A].greet()

			fun main { }
			""");
		Assert.Contains(outside.Diagnostics, diagnostic => diagnostic.Code == "KRN2106" && diagnostic.IsError);
		Assert.Null(outside.Code);

		var bare = CompileWithDiagnostics("""
			open type A {
				fun greet() -> str "A"
			}

			type B()[A] {
				fun call() -> A $[A]
			}

			fun main { }
			""");
		Assert.Contains(bare.Diagnostics, diagnostic => diagnostic.Code == "PAR003" && diagnostic.IsError);
		Assert.Null(bare.Code);
	}

	[Fact]
	public void Super_call_on_a_value_type_reports_unknown_parent()
	{
		var outcome = CompileWithDiagnostics("""
			unit Point(i32 x) {
				fun choose() -> i32 $[Point].x
			}

			fun main { }
			""");
		Assert.Contains(outcome.Diagnostics, diagnostic => diagnostic.Code == "KRN2103" && diagnostic.IsError);
		Assert.Null(outcome.Code);
	}

	private sealed record Outcome(CodegenResult? Code, IReadOnlyList<Diagnostic> Diagnostics);

	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	private static KValue Run(string source, string function, params KValue[] arguments) =>
		new Interpreter(Compile(source).Module, new NativeResolver(Catalog())).Run(function, arguments);

	private static CodegenResult Compile(string source)
	{
		var outcome = CompileWithDiagnostics(source);
		Assert.DoesNotContain(outcome.Diagnostics, diagnostic => diagnostic.IsError);
		Assert.NotNull(outcome.Code);
		return outcome.Code!;
	}

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
