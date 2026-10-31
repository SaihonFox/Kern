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
/// Batch 3 (M1–M3): `result`/`Ok`/`Err` и метки `ok`/`error`, однонаследственное
/// наследование, модули и `use`, пустой литерал `[]` с ожидаемым типом (Q21) и
/// формы итерации `loop` (пары dict, цикл без переменной, запись в существующую
/// локальную, деструктуризация в заголовке).
/// </summary>
public sealed class BatchThreeTests
{
	[Fact]
	public void Result_ok_and_err_construct_and_match()
	{
		const string source = """
			fun check(i32 x) -> result<i32, str> {
				if x > 0 { Ok(x) } else { Err("neg") }
			}

			fun ok_value() -> i32 {
				match check(5) {
					Ok(v) => v
					Err(e) => e.len
				}
			}

			fun err_length() -> i32 {
				match check(-1) {
					Ok(v) => v
					Err(e) => e.len
				}
			}
			""";
		Assert.Equal(KValue.I32(5), Run(source, "ok_value"));
		Assert.Equal(KValue.I32(3), Run(source, "err_length"));
	}

	[Fact]
	public void Result_type_is_inferred_from_if_branches()
	{
		const string source = """
			fun choose(bool flag) -> str {
				var value = if flag { Ok(11) } else { Err("no") }
				match value {
					Ok(v) => f"ok ${v}"
					Err(e) => f"err ${e}"
				}
			}
			""";
		Assert.Equal(KValue.Str("ok 11"), Run(source, "choose", KValue.Bool(true)));
		Assert.Equal(KValue.Str("err no"), Run(source, "choose", KValue.Bool(false)));
	}

	[Fact]
	public void Discarded_result_warns_and_discard_operator_silences()
	{
		var dropped = CompileWithDiagnostics("""
			fun parse(str text) -> result<i32, str> {
				Ok(1)
			}

			fun main {
				parse("x")
			}
			""");
		var warnings = dropped.Diagnostics.Where(diagnostic => diagnostic.Code == "KRN2305").ToList();
		Assert.Single(warnings);
		Assert.Equal(DiagnosticSeverity.Warning, warnings[0].Severity);

		var discarded = CompileWithDiagnostics("""
			fun parse(str text) -> result<i32, str> {
				Ok(1)
			}

			fun main {
				_ = parse("x")
				println("done")
			}
			""");
		Assert.DoesNotContain(discarded.Diagnostics, diagnostic => diagnostic.Code == "KRN2305");
		Assert.DoesNotContain(discarded.Diagnostics, diagnostic => diagnostic.IsError);
	}

	[Fact]
	public void Result_labels_and_variant_synonyms_are_equivalent()
	{
		const string source = """
			fun parse(str text) -> result<i32[ok], str[error]> {
				if text == "bad" { Err("bad") } else { Ok(7) }
			}

			fun plain_result() -> i32 {
				result<i32, str> value = parse("ok")
				match value {
					Ok(v) => v
					Err(e) => e.len
				}
			}

			fun labeled_result() -> i32 {
				result<i32[ok], str[error]> value = parse("ok")
				match value {
					Ok(v) => v + 1
					Err(e) => e.len
				}
			}

			fun ok_synonym() -> i32 {
				ok<i32> good = Ok(5)
				good.value
			}

			fun make_ok() -> ok<i32> {
				Ok(42)
			}

			fun make_ok_value() -> i32 {
				ok<i32> good = make_ok()
				good.value
			}

			fun make_err() -> error<str> {
				Err("беда")
			}

			fun err_message() -> str {
				error<str> bad = make_err()
				bad.error
			}
			""";
		Assert.Equal(KValue.I32(7), Run(source, "plain_result"));
		Assert.Equal(KValue.I32(8), Run(source, "labeled_result"));
		Assert.Equal(KValue.I32(5), Run(source, "ok_synonym"));
		Assert.Equal(KValue.I32(42), Run(source, "make_ok_value"));
		Assert.Equal(KValue.Str("беда"), Run(source, "err_message"));
	}

	[Fact]
	public void Derived_type_sees_inherited_field_method_and_property()
	{
		const string source = """
			open type Parent {
				i32 base = 2
				fun greet() -> str "parent"
				i32 tripled {
					get $base * 3
				}
			}

			type Child()[Parent] { }

			fun inherited_field() -> i32 {
				Child c()
				c.base
			}

			fun inherited_property() -> i32 {
				Child c()
				c.tripled
			}

			fun inherited_method() -> str {
				Child c()
				c.greet()
			}
			""";
		Assert.Equal(KValue.I32(2), Run(source, "inherited_field"));
		Assert.Equal(KValue.I32(6), Run(source, "inherited_property"));
		Assert.Equal(KValue.Str("parent"), Run(source, "inherited_method"));
	}

	[Fact]
	public void Own_method_wins_over_inherited_member()
	{
		const string source = """
			open type Parent {
				fun hello() -> str "parent"
			}

			type Child()[Parent] {
				fun hello() -> str "child"
			}

			fun child_hello() -> str {
				Child c()
				c.hello()
			}
			""";
		Assert.Equal(KValue.Str("child"), Run(source, "child_hello"));
	}

	[Fact]
	public void Three_level_chain_runs_parent_constructor_once()
	{
		const string source = """
			open type A(i32 a) {
				#i32 created = 0
				#fun bump() -> i32 {
					created += 1
					created
				}
				i32 aa = A.bump()
			}

			type B(i32 b)[A(b)] {
				i32 bb = b * 2
			}

			type C(i32 c)[B(c)] {
				i32 cc = c * 3
			}

			fun field_sum() -> i32 {
				C item(2)
				item.a + item.aa + item.bb + item.cc
			}

			fun parent_initializer_count() -> i32 {
				C item(2)
				A.created
			}
			""";
		Assert.Equal(KValue.I32(13), Run(source, "field_sum"));
		Assert.Equal(KValue.I32(1), Run(source, "parent_initializer_count"));
	}

	[Fact]
	public void Derived_value_is_parent_and_upcasts()
	{
		const string source = """
			open type Parent {
				i32 base = 2
				fun greet() -> str "parent"
			}

			type Child()[Parent] {
				str own = "c"
			}

			fun accepts(Parent p) -> str p.greet()

			fun upcast_greets() -> str {
				Child c()
				accepts(c)
			}

			fun is_parent() -> bool {
				Child c()
				Parent p = c
				(p is Parent) && (c is Parent)
			}
			""";
		Assert.Equal(KValue.Str("parent"), Run(source, "upcast_greets"));
		Assert.Equal(KValue.Bool(true), Run(source, "is_parent"));
	}

	[Fact]
	public void Abstract_type_cannot_be_constructed()
	{
		var outcome = CompileWithDiagnostics("""
			@type Shape {
				i32 id = 1
			}

			fun make() -> Shape {
				Shape()
			}
			""");
		Assert.Contains(outcome.Diagnostics, diagnostic => diagnostic.Code == "KRN2103" && diagnostic.IsError);
		Assert.Null(outcome.Code);
	}

	[Fact]
	public void Use_imports_a_name_from_another_module()
	{
		var project = CompileProject(
			("src/lib.kern", """
				fun inc(i32 n) -> i32 n + 1
				"""),
			("src/main.kern", """
				use lib[inc]
				fun answer() -> i32 inc(41)
				"""));
		Assert.Equal(KValue.I32(42), RunProject(project, "answer"));
	}

	[Fact]
	public void Use_reexports_imported_names()
	{
		var project = CompileProject(
			("src/base.kern", """
				fun inc(i32 n) -> i32 n + 1
				"""),
			("src/mid.kern", """
				use base[inc]
				"""),
			("src/main.kern", """
				use mid[inc]
				fun answer() -> i32 inc(41)
				"""));
		Assert.Equal(KValue.I32(42), RunProject(project, "answer"));
	}

	[Fact]
	public void Module_use_reports_unknown_plain_and_duplicate_imports()
	{
		var unknown = CompileProject(
			("src/main.kern", """
				use missing[thing]
				fun answer() -> i32 1
				"""));
		Assert.Contains("KRN1010", ProjectCodes(unknown));

		var plain = CompileProject(
			("src/lib.kern", """
				fun inc(i32 n) -> i32 n + 1
				"""),
			("src/main.kern", """
				use lib
				fun answer() -> i32 1
				"""));
		Assert.Contains("KRN1009", ProjectCodes(plain));

		var duplicate = CompileProject(
			("src/a.kern", """
				fun value() -> i32 1
				"""),
			("src/b.kern", """
				fun value() -> i32 2
				"""),
			("src/main.kern", """
				use a[value]
				use b[value]
				fun answer() -> i32 value()
				"""));
		Assert.Contains("KRN1011", ProjectCodes(duplicate));
	}

	[Fact]
	public void Empty_collection_literal_uses_expected_type()
	{
		const string source = """
			fun empty_list() -> list<i32> []
			fun empty_array() -> array<i32> []
			fun empty_dict() -> dict<str, i32> []

			fun local_list_len() -> i32 {
				list<i32> xs = []
				xs.len
			}

			fun local_array_len() -> i32 {
				array<i32> xs = []
				xs.len
			}

			fun local_dict_len() -> i32 {
				dict<str, i32> table = []
				table.len
			}

			fun returned_len() -> i32 {
				empty_list().len + empty_array().len + empty_dict().len
			}
			""";
		Assert.Equal(KValue.I32(0), Run(source, "local_list_len"));
		Assert.Equal(KValue.I32(0), Run(source, "local_array_len"));
		Assert.Equal(KValue.I32(0), Run(source, "local_dict_len"));
		Assert.Equal(KValue.I32(0), Run(source, "returned_len"));
	}

	[Fact]
	public void Dict_iteration_yields_key_value_pairs()
	{
		const string source = """
			fun total() -> i32 {
				dict<str, i32> ages = ["a": 1, "b": 2]
				i32 sum = 0
				loop (str key, i32 value) in ages {
					sum += value + key.len
				}
				sum
			}
			""";
		Assert.Equal(KValue.I32(5), Run(source, "total"));
	}

	[Fact]
	public void Loop_without_binding_and_bare_variable()
	{
		const string source = """
			fun counted() -> i32 {
				i32 n = 0
				loop 0..3 { n += 1 }
				n
			}

			fun last_written() -> i32 {
				i32 j = -1
				loop j in 0..3 { }
				j
			}
			""";
		Assert.Equal(KValue.I32(3), Run(source, "counted"));
		Assert.Equal(KValue.I32(2), Run(source, "last_written"));
	}

	[Fact]
	public void Tuple_destructuring_in_loop_header()
	{
		const string source = """
			fun total() -> i32 {
				list<(i32, str)> pairs = [(1, "a"), (2, "bb")]
				i32 sum = 0
				loop (i32 n, str text) in pairs {
					sum += n + text.len
				}
				sum
			}
			""";
		Assert.Equal(KValue.I32(6), Run(source, "total"));
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

	/// <summary>Полный конвейер одного файла с сохранением диагностик (для предупреждений и ошибок).</summary>
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

	private static string[] ProjectCodes(ProjectCompileResult project) =>
		[.. project.Diagnostics.Select(item => item.Diagnostic.Code)];
}
