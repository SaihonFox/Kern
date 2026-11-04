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
/// Модульные статические инициализаторы в проекте: VM выполняет один
/// <c>KbcModule.StaticInit</c>, поэтому инициализаторы всех файлов склеиваются в один
/// (раньше второй модуль давал <c>KRN1014</c>).
/// </summary>
public sealed class MultiModuleInitializerTests
{
	[Fact]
	public void Static_initializers_from_several_modules_all_run()
	{
		var project = CompileProject(
			("src/left.kern", """
				type Left {
					#i32 value = 40

					fun get() -> i32 value
				}
				"""),
			("src/right.kern", """
				type Right {
					#i32 value = 2

					fun get() -> i32 value
				}
				"""),
			("src/main.kern", """
				use left[Left]
				use right[Right]

				fun main() -> i32 Left().get() + Right().get()
				"""));

		Assert.Equal(KValue.I32(42), RunProject(project, "main"));
	}

	[Fact]
	public void Single_module_initializer_still_works()
	{
		var project = CompileProject(
			("src/main.kern", """
				type State {
					#i32 seed = 41

					fun get() -> i32 seed + 1
				}

				fun main() -> i32 State().get()
				"""));

		Assert.Equal(KValue.I32(42), RunProject(project, "main"));
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
