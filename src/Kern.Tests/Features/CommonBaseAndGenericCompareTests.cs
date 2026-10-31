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
/// Два среза 2026-11-01 (spec §3/§4): литерал списка выводит ближайший общий базовый
/// класс (<c>[Circle, Square]</c> → <c>Shape</c>, каждый элемент приводится upcast'ом),
/// а <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c> работают на одинаковых
/// <c>T[num]</c>/<c>T[inum]</c>/<c>T[fnum]</c> через <c>ValueCompare</c> (арифметика на
/// <c>T</c> этим не открывается, виртуальная диспетчеризация методов — вне среза).
/// </summary>
public sealed class CommonBaseAndGenericCompareTests
{
	[Fact]
	public void List_literal_infers_the_nearest_common_base_class()
	{
		const string source = """
			@type Shape {
				@fun area() -> f64
				fun kind() -> str "shape"
			}

			type Circle(f64 radius)[Shape] {
				~fun area() -> f64 3.14159 * radius ** 2
			}

			type Square(f64 side)[Shape] {
				~fun area() -> f64 side ** 2
			}

			fun f() -> i32 {
				list<Shape> shapes = [Circle(1.0), Square(2.0)]
				shapes.len
			}

			fun g() -> str {
				list<Shape> shapes = [Circle(1.0), Square(2.0)]
				shapes[1].kind()
			}
			""";

		Assert.Equal(KValue.I32(2), Run(source, "f"));
		Assert.Equal(KValue.Str("shape"), Run(source, "g"));
	}

	[Fact]
	public void List_literal_without_a_common_base_still_reports_KRN2101()
	{
		const string source = """
			type A { i32 x }
			type B { i32 y }

			fun main {
				list<A> xs = [A(), B()]
				println(xs.len)
			}
			""";

		var diagnostics = CompileWithDiagnostics(source).Diagnostics;
		var error = Assert.Single(diagnostics, diagnostic => diagnostic.IsError);
		Assert.Equal("KRN2101", error.Code);
		Assert.Contains("Нет общего типа элементов списка", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Generic_invariance_does_not_fake_a_common_base()
	{
		const string source = """
			type Shape {
				fun tag() -> str "shape"
			}

			type Box<T>()[Shape]

			fun f() -> i32 {
				list<Shape> shapes = [Box<i32>(), Box<str>()]
				shapes.len
			}
			""";

		// Box<i32> и Box<str> не объединяются в Box (инвариантность), но общий предок Shape есть.
		Assert.Equal(KValue.I32(2), Run(source, "f"));

		var diagnostics = CompileWithDiagnostics("""
			type Box<T>()

			fun main {
				list<Box<i32>> items = [Box<i32>(), Box<str>()]
				println(items.len)
			}
			""").Diagnostics;
		var error = Assert.Single(diagnostics, diagnostic => diagnostic.IsError);
		Assert.Equal("KRN2101", error.Code);
		Assert.Contains("Нет общего типа элементов списка", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Generic_parameter_comparisons_run_for_all_numeric_constraints()
	{
		const string source = """
			fun largest<T[num]>(T a, T b) -> T
				if a > b { a } else { b }

			fun smallest<T[inum]>(T a, T b) -> T
				if a < b { a } else { b }

			fun at_most<T[fnum]>(T a, T b) -> bool
				a <= b

			fun at_least<T[num]>(T a, T b) -> bool
				a >= b
			""";

		Assert.Equal(KValue.I32(9), Run(source, "largest", KValue.I32(3), KValue.I32(9)));
		Assert.Equal(KValue.F64(2.5), Run(source, "largest", KValue.F64(2.5), KValue.F64(1.5)));
		Assert.Equal(KValue.I32(4), Run(source, "smallest", KValue.I32(4), KValue.I32(7)));
		Assert.Equal(KValue.Bool(true), Run(source, "at_most", KValue.F64(1.5), KValue.F64(2.5)));
		Assert.Equal(KValue.Bool(false), Run(source, "at_least", KValue.F64(1.5), KValue.F64(2.5)));
	}

	[Fact]
	public void Unconstrained_generic_comparison_still_errors()
	{
		var diagnostics = CompileWithDiagnostics("""
			fun f<T>(T a, T b) -> bool a > b
			""").Diagnostics;
		var error = Assert.Single(diagnostics, diagnostic => diagnostic.IsError);
		Assert.Equal("KRN2101", error.Code);
		Assert.Contains("Оператор '>' не применим к 'T' и 'T'", error.Message, StringComparison.Ordinal);
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
