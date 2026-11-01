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
/// <c>kern.env</c>, <c>file.*</c> и <c>kern.path</c> (spec §16): файловые операции возвращают
/// <c>result&lt;T, str&gt;</c>, то есть нативный <c>KResult</c> превращается в union
/// <c>Ok&lt;T&gt;</c>/<c>Err&lt;E&gt;</c> и сопоставляется в <c>match</c>.
/// </summary>
public sealed class EnvFileModuleTests
{
	[Fact]
	public void Env_reads_and_reports_platform_values()
	{
		const string variable = "KERN_TEST_ENV_VALUE";
		System.Environment.SetEnvironmentVariable(variable, "значение");
		try
		{
			var source = $$"""
				fun value() -> str? { env.get("{{variable}}") }
				fun missing() -> str? { env.get("KERN_TEST_ENV_DEFINITELY_MISSING") }
				fun platform() -> bool { env.os.len > 0 }
				""";

			Assert.Equal(KValue.Str("значение"), Run(source, "value"));
			Assert.Equal(KValue.Nil, Run(source, "missing"));
			Assert.Equal(KValue.Bool(true), Run(source, "platform"));
		}
		finally
		{
			System.Environment.SetEnvironmentVariable(variable, null);
		}
	}

	[Fact]
	public void File_round_trips_text_and_reports_errors_as_err()
	{
		string directory = NewDirectory();
		string path = Slash(System.IO.Path.Combine(directory, "note.txt"));
		try
		{
			var source = $$"""
				fun written() -> bool {
					match file.write_text("{{path}}", "привет") {
						Ok(_) => true
						Err(_) => false
					}
				}
				fun text() -> str {
					match file.read_text("{{path}}") {
						Ok(value) => value
						Err(e) => e
					}
				}
				fun missing_is_err() -> bool {
					match file.read_text("{{Slash(System.IO.Path.Combine(directory, "absent.txt"))}}") {
						Ok(_) => false
						Err(e) => e.len > 0
					}
				}
				fun appended() -> bool {
					match file.append_text("{{path}}", "!") {
						Ok(_) => true
						Err(_) => false
					}
				}
				fun exists() -> bool { file.exists("{{path}}") }
				fun deleted() -> bool {
					match file.delete("{{path}}") {
						Ok(_) => true
						Err(_) => false
					}
				}
				""";

			Assert.Equal(KValue.Bool(true), Run(source, "written"));
			Assert.Equal(KValue.Str("привет"), Run(source, "text"));
			Assert.Equal(KValue.Bool(true), Run(source, "missing_is_err"));
			Assert.Equal(KValue.Bool(true), Run(source, "appended"));
			Assert.Equal(KValue.Bool(true), Run(source, "exists"));
			Assert.Equal(KValue.Bool(true), Run(source, "deleted"));
			Assert.Equal(KValue.Bool(false), Run(source, "exists"));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void File_lines_and_directory_listing()
	{
		string directory = NewDirectory();
		string path = Slash(System.IO.Path.Combine(directory, "lines.txt"));
		try
		{
			var source = $$"""
				fun written() -> bool {
					match file.write_lines("{{path}}", ["раз", "два"]) {
						Ok(_) => true
						Err(_) => false
					}
				}
				fun lines() -> i32 {
					match file.read_lines("{{path}}") {
						Ok(values) => values.len
						Err(_) => -1
					}
				}
				fun listed() -> bool {
					match file.list_dir("{{Slash(directory)}}") {
						Ok(names) => names.len == 1
						Err(_) => false
					}
				}
				""";

			Assert.Equal(KValue.Bool(true), Run(source, "written"));
			Assert.Equal(KValue.I32(2), Run(source, "lines"));
			Assert.Equal(KValue.Bool(true), Run(source, "listed"));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void Path_operations_are_lexical()
	{
		const string source = """
			fun joined() -> str { path.join("a", "b.txt") }
			fun extension() -> str { path.ext("b.txt") }
			fun name() -> str { path.file_name("a/b.txt") }
			fun directory() -> str { path.dir_name("a/b.txt") }
			fun normalized() -> str { path.normalize("a/./b/../c.txt") }
			fun relative() -> bool { path.is_absolute("a/b") }
			""";

		Assert.Equal(KValue.Str(System.IO.Path.Combine("a", "b.txt")), Run(source, "joined"));
		Assert.Equal(KValue.Str(".txt"), Run(source, "extension"));
		Assert.Equal(KValue.Str("b.txt"), Run(source, "name"));
		Assert.Equal(KValue.Str("a"), Run(source, "directory"));
		Assert.Equal(
			KValue.Str(System.IO.Path.Combine("a", "c.txt")),
			Run(source, "normalized"));
		Assert.Equal(KValue.Bool(false), Run(source, "relative"));
	}

	private static string NewDirectory()
	{
		string directory = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			$"kern_m7b_{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		return directory;
	}

	private static string Slash(string path) => path.Replace('\\', '/');

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
