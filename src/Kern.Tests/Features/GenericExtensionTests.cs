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
/// Полиморфные extension-приёмники (spec §10): `fun list&lt;T&gt;.first_or(T) -&gt; T`,
/// generic-свойства и операторы; `T` выводится из фактического типа приёмника.
/// </summary>
public sealed class GenericExtensionTests
{
	[Fact]
	public void Generic_extension_function_infers_element_type()
	{
		const string source = """
			fun list<T>.first_or(T fallback) -> T {
				if $is_empty { fallback } else { $get(0) }
			}

			fun numbers() -> i32 [7, 8].first_or(0)
			fun texts() -> str {
				list<str> empty = []
				empty.first_or("none")
			}
			""";

		Assert.Equal(KValue.I32(7), Run(source, "numbers"));
		Assert.Equal(KValue.Str("none"), Run(source, "texts"));
	}

	[Fact]
	public void Generic_extension_property_substitutes_receiver_argument()
	{
		const string source = """
			unit Box<T>(T inner)
			T Box<T>.value $inner

			fun number() -> i32 Box(5).value
			fun text() -> str Box("hi").value
			""";

		Assert.Equal(KValue.I32(5), Run(source, "number"));
		Assert.Equal(KValue.Str("hi"), Run(source, "text"));
	}

	[Fact]
	public void Generic_extension_operator_runs()
	{
		const string source = """
			unit Box<T>(T inner)
			op Box<T>.+(Box<T> other) -> Box<T> Box($inner)

			fun value() -> i32 {
				Box<i32> sum = Box(1) + Box(2)
				sum.inner
			}
			""";

		Assert.Equal(KValue.I32(1), Run(source, "value"));
	}

	[Fact]
	public void Generic_extension_falls_back_to_the_parameter_on_an_empty_list()
	{
		const string source = """
			fun list<T>.first_or(T fallback) -> T {
				if $is_empty { fallback } else { $get(0) }
			}

			fun value() -> i32 {
				list<i32> empty = []
				empty.first_or(42)
			}
			""";

		Assert.Equal(KValue.I32(42), Run(source, "value"));
	}

	[Fact]
	public void Generic_extension_is_imported_by_its_member_name()
	{
		var project = CompileProject(
			("src/lib.kern", """
				fun list<T>.first_or(T fallback) -> T {
					if $is_empty { fallback } else { $get(0) }
				}
				"""),
			("src/main.kern", """
				use lib[first_or]
				fun answer() -> i32 [3, 4].first_or(0)
				"""));

		Assert.Equal(KValue.I32(3), RunProject(project, "answer"));
	}

	[Fact]
	public void Concrete_extension_and_instance_member_still_win()
	{
		const string source = """
			open type Ball {
				fun hello() -> str "instance"
			}

			fun list<T>.first_or(T fallback) -> T fallback
			fun list<i32>.first_or(i32 fallback) -> i32 99
			fun Ball.hello() -> str "extension"

			fun member() -> str Ball().hello()
			fun concrete() -> i32 {
				list<i32> xs = [1, 2]
				xs.first_or(9)
			}
			""";

		Assert.Equal(KValue.Str("instance"), Run(source, "member"));
		Assert.Equal(KValue.I32(99), Run(source, "concrete"));
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
