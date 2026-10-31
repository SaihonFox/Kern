using Kern.Compiler.CodeGen;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Инфраструктурный smoke-тест нового набора: конвейер парсер → объявления → binder →
/// codegen → VM работает.Тесты фич пишутся пакетами после реализации пяти функций.
/// </summary>
public sealed class SmokeTests
{
	[Fact]
	public void Pipeline_runs_a_trivial_function()
	{
		const string source = "fun f() -> i32 { 1 + 2 }";

		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);

		var bound = new Binder(module, table).Bind();
		Assert.DoesNotContain(bound.Diagnostics, diagnostic => diagnostic.IsError);
		var generated = new CodeGenerator().Generate(bound);
		Assert.DoesNotContain(generated.Diagnostics, diagnostic => diagnostic.IsError);

		Assert.Equal(KValue.I32(3), new Interpreter(generated.Module).Run("f", []));
	}
}
