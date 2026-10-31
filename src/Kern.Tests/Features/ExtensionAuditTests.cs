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
/// Аудит расширений типов (spec §10): инстанс-член важнее расширения, расширения на
/// встроенных типах и generic-приёмниках, свойства/операторы, перегрузки, конфликты
/// <c>KRN1400</c>/<c>KRN1407</c>, импорт расширений из другого модуля.
/// </summary>
public sealed class ExtensionAuditTests
{
	[Fact]
	public void Instance_member_wins_over_an_extension_with_the_same_name()
	{
		const string source = """
			open type Ball {
				fun hello() -> str "instance"
			}

			fun Ball.hello() -> str "extension"

			fun f() -> str {
				Ball().hello()
			}
			""";

		Assert.Equal(KValue.Str("instance"), Run(source, "f"));
	}

	[Fact]
	public void Builtin_extension_function_and_property_use_dollar_receiver()
	{
		const string source = """
			fun str.shout() -> str { $ + "!" }
			str str.twice $ + $

			fun shout() -> str "hi".shout()
			fun twice() -> str "ab".twice
			""";

		Assert.Equal(KValue.Str("hi!"), Run(source, "shout"));
		Assert.Equal(KValue.Str("abab"), Run(source, "twice"));
	}

	[Fact]
	public void Generic_receiver_supports_methods_and_properties()
	{
		const string source = """
			fun list<i32>.total() -> i32 { $.len }
			i32 list<i32>.count $.len

			fun total() -> i32 [1, 2, 3].total()
			fun count() -> i32 [4, 5].count
			""";

		Assert.Equal(KValue.I32(3), Run(source, "total"));
		Assert.Equal(KValue.I32(2), Run(source, "count"));
	}

	[Fact]
	public void Extension_overloads_resolve_by_argument_types()
	{
		const string source = """
			fun i32.plus(i32 n) -> i32 { $ + n }
			fun i32.plus(str s) -> str { f"${s}${$}" }

			fun number() -> i32 10.plus(5)
			fun text() -> str 10.plus("x=")
			""";

		Assert.Equal(KValue.I32(15), Run(source, "number"));
		Assert.Equal(KValue.Str("x=10"), Run(source, "text"));
	}

	[Fact]
	public void Duplicate_extension_signature_reports_KRN1400()
	{
		const string source = """
			fun str.foo() -> str "a"
			fun str.foo() -> str "b"
			fun main { println("x".foo()) }
			""";

		var outcome = CompileWithDiagnostics(source);
		Assert.Contains(outcome.Diagnostics, diagnostic => diagnostic.Code == "KRN1400");
	}

	[Fact]
	public void Function_and_property_name_clash_reports_KRN1407()
	{
		const string source = """
			fun str.foo() -> str "a"
			str str.foo "b"
			fun main { println("x".foo) }
			""";

		var outcome = CompileWithDiagnostics(source);
		Assert.Contains(outcome.Diagnostics, diagnostic => diagnostic.Code == "KRN1407");
	}

	[Fact]
	public void Operator_extensions_work_for_primitive_left_and_user_type()
	{
		const string source = """
			unit Vec(f64 x)

			op i32.*(Vec v) -> Vec {
				Vec($ * v.x)
			}

			op Vec.+(i32 n) -> Vec {
				Vec($.x + 1.0)
			}

			fun scaled() -> f64 {
				Vec v = 3 * Vec(2.0)
				v.x
			}

			fun shifted() -> f64 {
				Vec v = Vec(2.0) + 10
				v.x
			}
			""";

		Assert.Equal(KValue.F64(6.0), Run(source, "scaled"));
		Assert.Equal(KValue.F64(3.0), Run(source, "shifted"));
	}

	[Fact]
	public void Imported_extension_is_visible_by_its_member_name()
	{
		var project = CompileProject(
			("src/lib.kern", """
				fun str.shout() -> str { $ + "!" }
				"""),
			("src/main.kern", """
				use lib[shout]
				fun answer() -> str "hi".shout()
				"""));

		Assert.Equal(KValue.Str("hi!"), RunProject(project, "answer"));
	}

	[Fact]
	public void Dollar_on_the_last_line_of_a_property_does_not_swallow_the_next_declaration()
	{
		const string source = """
			str str.twice $ + $
			i32 str.doubled_len $.len * 2

			fun twice() -> str "ab".twice
			fun length() -> i32 "abc".doubled_len
			""";

		Assert.Equal(KValue.Str("abab"), Run(source, "twice"));
		Assert.Equal(KValue.I32(6), Run(source, "length"));
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
