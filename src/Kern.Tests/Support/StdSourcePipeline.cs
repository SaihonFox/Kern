using Kern.Binding.Resolution;
using Kern.Cli.Infrastructure;
using Kern.Tests.Features.Support;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Support;

/// <summary>
/// Компиляция через CLI-конвейер с автоподключением <c>Kern.Std</c>: нужна для std-модулей,
/// которые теперь написаны на Kern (<c>kern.csv</c>, <c>kern.parse</c>, ...), а не на C#.
/// </summary>
internal static class StdSourcePipeline
{
	public static KValue Run(string source, string function, params KValue[] arguments)
	{
		CompileResult result = SourceCompiler.CompileFiles(
			[("app.kern", source)],
			"app",
			StdPipeline.Catalog(),
			CompileOptions.Default);
		Assert.NotNull(result.Program);
		CompiledProgram program = result.Program!;
		return new Interpreter(program.Module, new NativeResolver(program.Catalog)).Run(function, arguments);
	}

	public static void Compiles(string source)
	{
		CompileResult result = SourceCompiler.CompileFiles(
			[("app.kern", source)],
			"app",
			StdPipeline.Catalog(),
			CompileOptions.Default);
		Assert.True(result.Program is not null, string.Join('\n', result.ProjectMessages));
	}
}
