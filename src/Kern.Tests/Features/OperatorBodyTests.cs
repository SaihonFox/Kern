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
/// Expression-bodied операторы и индексаторы: `op +(…) -> T expr` и `op [](…) -> T expr`
/// без блочного тела (spec §3/§5).
/// </summary>
public sealed class OperatorBodyTests
{
	[Fact]
	public void Expression_bodied_indexer_and_binary_operator_run()
	{
		const string source = """
			unit Pair(i32 x, i32 y) {
				op [](i32 i) -> i32 if i == 0 { $x } else { $y }
				op +(Pair other) -> i32 $x + other.x
			}

			fun index() -> i32 {
				Pair p(3, 4)
				p[1]
			}

			fun sum() -> i32 {
				Pair p(3, 4)
				p + Pair(10, 0)
			}
			""";

		Assert.Equal(KValue.I32(4), Run(source, "index"));
		Assert.Equal(KValue.I32(13), Run(source, "sum"));
	}

	[Fact]
	public void Expression_bodied_operator_on_a_type_dispatches()
	{
		const string source = """
			open type A {
				op +(A other) -> str { "A" }
			}

			type B()[A] {
				~op +(B other) -> str "B"
			}

			fun value() -> str {
				A a = B()
				a + a
			}
			""";

		Assert.Equal(KValue.Str("B"), Run(source, "value"));
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
