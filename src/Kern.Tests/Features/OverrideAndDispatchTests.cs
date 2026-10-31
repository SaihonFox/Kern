using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Bytecode;
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
/// Q29 (spec §2): <c>~fun</c> законен только при унаследованном <c>open</c>/<c>@fun</c>
/// методе с тем же именем и числом параметров. Q30: геттеры/сеттеры свойств,
/// пользовательские операторы и индексаторы класса диспатчатся по runtime-типу
/// получателя (<c>CallVirtual</c>), а у <c>unit</c> остаются статическими.
/// </summary>
public sealed class OverrideAndDispatchTests
{
	[Fact]
	public void Override_without_base_reports_krn2101()
	{
		var diagnostics = CompileWithDiagnostics("""
			type B() {
				~fun missing() -> i32 1
			}

			fun main { }
			""").Diagnostics;

		var error = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "KRN2101");
		Assert.Contains("B.missing", error.Message, StringComparison.Ordinal);
		Assert.Contains("~", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Override_of_open_method_compiles_and_dispatches()
	{
		const string source = """
			open type A {
				open fun area() -> i32 1
			}

			type B()[A] {
				~fun area() -> i32 2
			}

			fun go() -> i32 {
				A a = B()
				a.area()
			}
			""";

		Assert.Equal(KValue.I32(2), Run(source, "go"));
	}

	[Fact]
	public void Override_of_abstract_method_compiles()
	{
		const string source = """
			@type A {
				@fun area() -> i32
			}

			type B()[A] {
				~fun area() -> i32 3
			}

			fun go() -> i32 {
				A a = B()
				a.area()
			}
			""";

		Assert.Equal(KValue.I32(3), Run(source, "go"));
	}

	[Fact]
	public void Override_with_wrong_arity_reports_krn2101()
	{
		var diagnostics = CompileWithDiagnostics("""
			open type A {
				open fun area(i32 n) -> i32 n
			}

			type B()[A] {
				~fun area() -> i32 2
			}

			fun main { }
			""").Diagnostics;

		var error = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "KRN2101");
		Assert.Contains("B.area", error.Message, StringComparison.Ordinal);
		Assert.Contains("'open'/'@fun'", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Override_of_non_open_method_reports_krn2101()
	{
		var diagnostics = CompileWithDiagnostics("""
			open type A {
				fun area() -> i32 1
			}

			type B()[A] {
				~fun area() -> i32 2
			}

			fun main { }
			""").Diagnostics;

		var error = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "KRN2101");
		Assert.Contains("B.area", error.Message, StringComparison.Ordinal);
		Assert.Contains("~", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Operator_dispatches_by_runtime_type()
	{
		const string source = """
			open type A {
				op +(A other) -> str { "A" }
			}

			type B()[A] {
				op +(B other) -> str { "B" }
			}

			fun go() -> str {
				A a = B()
				a + a
			}
			""";

		Assert.Equal(KValue.Str("B"), Run(source, "go"));
	}

	[Fact]
	public void Property_getter_dispatches_by_runtime_type()
	{
		const string source = """
			open type A {
				i32 v => 1
			}

			type B()[A] {
				i32 v => 2
			}

			fun go() -> i32 {
				A a = B()
				a.v
			}
			""";

		Assert.Equal(KValue.I32(2), Run(source, "go"));
	}

	[Fact]
	public void Property_setter_dispatches_by_runtime_type()
	{
		const string source = """
			open type A {
				i32 _v
				i32 v {
					get $_v
					set(value) { $_v = value }
				}
			}

			type B()[A] {
				i32 v {
					get $_v
					set(value) { $_v = value * 10 }
				}
			}

			fun go() -> i32 {
				A a = B()
				a.v = 3
				a.v
			}
			""";

		Assert.Equal(KValue.I32(30), Run(source, "go"));
	}

	[Fact]
	public void Indexer_dispatches_by_runtime_type()
	{
		const string source = """
			open type A {
				op [](i32 i) -> str { "A" }
			}

			type B()[A] {
				op [](i32 i) -> str { "B" }
			}

			fun go() -> str {
				A a = B()
				a[0]
			}
			""";

		Assert.Equal(KValue.Str("B"), Run(source, "go"));
	}

	[Fact]
	public void Unit_operator_and_property_stay_static()
	{
		const string source = """
			unit Point(i32 x) {
				op +(Point other) -> str { "point" }
				i32 doubled => x * 2
			}

			fun sum() -> str {
				Point p(2)
				p + p
			}

			fun doubled() -> i32 {
				Point p(4)
				p.doubled
			}
			""";

		Assert.Equal(KValue.Str("point"), Run(source, "sum"));
		Assert.Equal(KValue.I32(8), Run(source, "doubled"));
		Assert.DoesNotContain(OpCode.CallVirtual, OpCodes(Compile(source)));
	}

	[Fact]
	public void Operator_and_property_override_with_a_base_run()
	{
		const string source = """
			open type A {
				op +(A other) -> str { "A" }
				i32 v => 1
			}

			type B()[A] {
				~op +(B other) -> str "B"
				~i32 v => 2
			}

			fun apply() -> str {
				A a = B()
				a + a
			}

			fun prop() -> i32 {
				A a = B()
				a.v
			}
			""";

		Assert.Equal(KValue.Str("B"), Run(source, "apply"));
		Assert.Equal(KValue.I32(2), Run(source, "prop"));
	}

	[Fact]
	public void Operator_and_property_override_without_a_base_report_KRN2101()
	{
		const string source = """
			type C {
				~op +(C other) -> str "C"
				~i32 v => 2
			}

			fun main { println(C().v) }
			""";

		var outcome = CompileWithDiagnostics(source);
		var errors = outcome.Diagnostics.Where(diagnostic => diagnostic.Code == "KRN2101").ToList();
		Assert.Equal(2, errors.Count);
		Assert.Contains(errors, diagnostic => diagnostic.Message.Contains("Оператор", StringComparison.Ordinal));
		Assert.Contains(errors, diagnostic => diagnostic.Message.Contains("Свойство", StringComparison.Ordinal));
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

	/// <summary>Полный конвейер без интерпретатора: диагностики и байткод.</summary>
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

	/// <summary>Все опкоды всех функций модуля (для проверки выбора Call/CallVirtual).</summary>
	private static List<OpCode> OpCodes(CodegenResult code)
	{
		var result = new List<OpCode>();
		foreach (var function in code.Module.FunctionPool)
		{
			var instructions = function.Code;
			for (var offset = 0; offset < instructions.Length;)
			{
				var op = (OpCode)instructions[offset];
				result.Add(op);
				offset += 1 + OpCodeInfo.OperandCount(op);
			}
		}

		return result;
	}
}
