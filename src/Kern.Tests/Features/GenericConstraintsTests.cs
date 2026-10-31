using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Compilation;
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
/// Generic-ограничения (spec §14) и подстановка union-алиасов: `ctor()`, class-ограничение,
/// `T[num]`, `Option&lt;i32&gt;` (включая импорт и вложенные типы).
/// </summary>
public sealed class GenericConstraintsTests
{
	[Fact]
	public void Constructor_constraint_accepts_a_unit_with_a_constructor()
	{
		const string source = """
			unit Good(i32 x)
			fun pass<T[ctor()]>(T value) -> T value

			fun value() -> i32 pass(Good(1)).x
			""";

		Assert.Equal(KValue.I32(1), Run(source, "value"));
	}

	[Fact]
	public void Constructor_constraint_rejects_a_primitive()
	{
		var outcome = CompileWithDiagnostics("""
			unit Good(i32 x)
			fun pass<T[ctor()]>(T value) -> T value
			fun main { println(pass(5)) }
			""");

		Assert.Contains(outcome.Diagnostics, diagnostic => diagnostic.Code == "KRN1401");
	}

	[Fact]
	public void Constructor_constraint_rejects_an_abstract_type()
	{
		var outcome = CompileWithDiagnostics("""
			@type Shape { @fun area() -> f64 }
			fun pass<T[ctor()]>(T value) -> T value
			fun take(Shape s) -> Shape pass(s)
			""");

		Assert.Contains(outcome.Diagnostics, diagnostic => diagnostic.Code == "KRN1401");
	}

	[Fact]
	public void Class_constraint_accepts_a_derived_type_and_rejects_an_unrelated_one()
	{
		const string good = """
			open type Base { i32 id = 1 }
			type Derived()[Base] { }
			fun pass<T[Base]>(T value) -> T value
			fun value() -> i32 pass(Derived()).id
			""";
		Assert.Equal(KValue.I32(1), Run(good, "value"));

		var bad = CompileWithDiagnostics("""
			open type Base { i32 id = 1 }
			type Other { i32 id = 2 }
			fun pass<T[Base]>(T value) -> T value
			fun value() -> i32 pass(Other()).id
			""");
		Assert.Contains(bad.Diagnostics, diagnostic => diagnostic.Code == "KRN1401");
	}

	[Fact]
	public void Generic_union_alias_substitutes_alternatives_and_matches()
	{
		const string source = """
			unit Some<T>(T value)
			unit Nothing
			type Option<T> = [Some<T>, Nothing]

			fun pick(Option<i32> o) -> i32 {
				match o {
					Some(v) => v
					Nothing => 0
				}
			}

			fun some() -> i32 pick(Some(5))
			fun nothing() -> i32 pick(Nothing())
			""";

		Assert.Equal(KValue.I32(5), Run(source, "some"));
		Assert.Equal(KValue.I32(0), Run(source, "nothing"));
	}

	[Fact]
	public void Generic_union_alias_works_nested_in_a_list()
	{
		const string source = """
			unit Some<T>(T value)
			unit Nothing
			type Option<T> = [Some<T>, Nothing]

			fun first(list<Option<i32>> xs) -> i32 {
				match xs.get(0) {
					Some(v) => v
					Nothing => 0
				}
			}

			fun value() -> i32 {
				list<Option<i32>> xs = [Some(7)]
				first(xs)
			}
			""";

		Assert.Equal(KValue.I32(7), Run(source, "value"));
	}

	[Fact]
	public void Generic_union_alias_imports_with_type_arguments()
	{
		var project = CompileProject(
			("src/lib.kern", """
				unit Some<T>(T value)
				unit Nothing
				type Option<T> = [Some<T>, Nothing]
				"""),
			("src/main.kern", """
				use lib[Option, Some, Nothing]

				fun answer() -> i32 {
					Option<i32> value = Some(9)
					match value {
						Some(v) => v
						Nothing => 0
					}
				}
				"""));

		Assert.Equal(KValue.I32(9), RunProject(project, "answer"));
	}

	[Fact]
	public void Generic_union_alias_wrong_arity_reports_KRN1002()
	{
		var outcome = CompileWithDiagnostics("""
			unit Some<T>(T value)
			unit Nothing
			type Option<T> = [Some<T>, Nothing]
			fun bad(Option<i32, str> value) -> i32 1
			""");

		Assert.Contains(outcome.Diagnostics, diagnostic => diagnostic.Code == "KRN1002");
	}

	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	private static KValue Run(string source, string function, params KValue[] arguments) =>
		new Interpreter(Compile(source).Module, new NativeResolver(Catalog())).Run(function, arguments);

	private static ProjectCompileResult CompileProject(params (string Path, string Text)[] files) =>
		ProjectCompiler.Compile(
			[.. files.Select(file => new ProjectSourceFile(file.Path, file.Text))],
			"app",
			Catalog());

	private static KValue RunProject(ProjectCompileResult project, string function)
	{
		var problems = string.Join(
			Environment.NewLine,
			project.Diagnostics.Select(item => $"{item.Path}: {item.Diagnostic.Code} {item.Diagnostic.Message}"));
		Assert.True(project.Success, problems);
		var generated = new CodeGenerator().Generate(project.Module!);
		Assert.DoesNotContain(generated.Diagnostics, diagnostic => diagnostic.IsError);
		return new Interpreter(generated.Module, new NativeResolver(Catalog())).Run(function, []);
	}

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
