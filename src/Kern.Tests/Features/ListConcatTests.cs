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
/// Конкатенация списков (spec §3, `list + list`): результат — новый `list<T>`,
/// исходные списки не меняются; тот же путь использует `items + [item]`.
/// </summary>
public sealed class ListConcatTests
{
	[Fact]
	public void List_concat_creates_a_new_list_and_keeps_sources()
	{
		const string source = """
			fun length() -> i32 {
				list<i32> a = [1, 2]
				list<i32> b = [3, 4]
				list<i32> c = a + b
				c.len
			}

			fun last() -> i32 {
				list<i32> a = [1, 2]
				list<i32> b = [3, 4]
				list<i32> c = a + b
				c[3]
			}

			fun source_len() -> i32 {
				list<i32> a = [1, 2]
				list<i32> b = [3, 4]
				list<i32> c = a + b
				a.len
			}
			""";

		Assert.Equal(KValue.I32(4), Run(source, "length"));
		Assert.Equal(KValue.I32(4), Run(source, "last"));
		Assert.Equal(KValue.I32(2), Run(source, "source_len"));
	}

	[Fact]
	public void Appending_a_single_element_list_reassigns_the_field()
	{
		const string source = """
			fun push_like() -> i32 {
				list<i32> xs = [1]
				xs = xs + [2]
				xs.len
			}

			fun element() -> i32 {
				list<i32> xs = [1]
				xs = xs + [2]
				xs[1]
			}
			""";

		Assert.Equal(KValue.I32(2), Run(source, "push_like"));
		Assert.Equal(KValue.I32(2), Run(source, "element"));
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
