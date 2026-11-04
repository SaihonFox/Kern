using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Compilation;
using Kern.Compiler.Semantics;
using Kern.Tests.Features.Support;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Квалифицированный доступ к source-модулям (spec §5): <c>use app.util[...]</c> открывает
/// <c>util.member(...)</c> так же, как нативные <c>kern.*</c>-модули.
/// </summary>
public sealed class ModuleQualifiedAccessTests
{
	[Fact]
	public void Qualified_call_reaches_imported_source_module()
	{
		var project = CompileProject(
			("src/util.kern", """
				fun greet(str name) -> str f"hi ${name}"
				"""),
			("src/main.kern", """
				use util[greet]

				fun greeting() -> str util.greet("kern")

				fun main() -> i32 0
				"""));

		Assert.Equal(KValue.Str("hi kern"), RunProject(project, "greeting"));
	}

	[Fact]
	public void Unqualified_import_keeps_working_next_to_qualified_access()
	{
		var project = CompileProject(
			("src/util.kern", """
				fun twice(i32 x) -> i32 x * 2
				"""),
			("src/main.kern", """
				use util[twice]

				fun main() -> i32 twice(2) + util.twice(3)
				"""));

		Assert.Equal(KValue.I32(10), RunProject(project, "main"));
	}

	[Fact]
	public void Wildcard_import_also_allows_qualified_access()
	{
		var project = CompileProject(
			("src/util.kern", """
				fun twice(i32 x) -> i32 x * 2
				"""),
			("src/main.kern", """
				use util.*

				fun main() -> i32 util.twice(21)
				"""));

		Assert.Equal(KValue.I32(42), RunProject(project, "main"));
	}

	[Fact]
	public void Unknown_member_of_source_module_reports_KRN2103()
	{
		var project = CompileProject(
			("src/util.kern", """
				fun greet(str name) -> str name
				"""),
			("src/main.kern", """
				use util[greet]

				fun main() -> str util.missing("x")
				"""));

		Assert.Contains(project.Diagnostics, item =>
			item.Diagnostic.Code == "KRN2103" &&
			item.Diagnostic.Message.Contains("не экспортирует функцию 'missing'", StringComparison.Ordinal));
	}

	[Fact]
	public void Local_variable_shadows_module_name()
	{
		var project = CompileProject(
			("src/util.kern", """
				fun twice(i32 x) -> i32 x * 2
				"""),
			("src/main.kern", """
				use util[twice]

				type util(i32 value) {
					fun get() -> i32 $value
				}

				fun main() -> i32 {
					util local = util(7)
					local.get()
				}
				"""));

		Assert.Equal(KValue.I32(7), RunProject(project, "main"));
	}

	private static NativeCatalog Catalog() => StdPipeline.Catalog();

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
}
