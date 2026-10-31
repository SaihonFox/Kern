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

namespace Kern.Tests.Execution;

/// <summary>
/// Elvis <c>val ?: default</c> (spec §3/§4): значение справа берётся, когда слева <c>nil</c>
/// или <c>none</c>; для <c>T?</c> результат — <c>T</c>, левая часть вычисляется один раз,
/// в правой ветви допустим <c>ret</c>, несовместимый тип — <c>KRN2102</c>.
/// </summary>
public sealed class ElvisTests
{
	[Fact]
	public void Value_and_nil_choose_the_branch()
	{
		const string source = """
			fun with_value() -> i32 {
				i32? x = 5
				x ?: 10
			}

			fun with_nil() -> i32 {
				i32? x = nil
				x ?: 10
			}

			fun uninitialized() -> i32 {
				i32? x
				x ?: 7
			}
			""";

		Assert.Equal(KValue.I32(5), Run(source, "with_value"));
		Assert.Equal(KValue.I32(10), Run(source, "with_nil"));
		Assert.Equal(KValue.I32(7), Run(source, "uninitialized"));
	}

	[Fact]
	public void Left_side_is_evaluated_exactly_once()
	{
		const string source = """
			type Counter {
				#i32 n = 0
			}

			fun bump() -> i32? {
				Counter.n = Counter.n + 1
				nil
			}

			fun f() -> i32 {
				i32 value = bump() ?: 7
				Counter.n * 100 + value
			}
			""";

		Assert.Equal(KValue.I32(107), Run(source, "f"));
	}

	[Fact]
	public void Chained_elvis_uses_the_first_present_value()
	{
		const string source = """
			fun f() -> i32 {
				i32? a = nil
				i32? b = nil
				a ?: b ?: 9
			}

			fun g() -> i32 {
				i32? a = nil
				i32? b = 3
				a ?: b ?: 9
			}
			""";

		Assert.Equal(KValue.I32(9), Run(source, "f"));
		Assert.Equal(KValue.I32(3), Run(source, "g"));
	}

	[Fact]
	public void Nullable_string_default_is_used_for_nil()
	{
		const string source = """
			fun f() -> str {
				str? s = nil
				s ?: "fallback"
			}

			fun g() -> str {
				str? s = "ok"
				s ?: "fallback"
			}
			""";

		Assert.Equal(KValue.Str("fallback"), Run(source, "f"));
		Assert.Equal(KValue.Str("ok"), Run(source, "g"));
	}

	[Fact]
	public void Return_branch_is_allowed()
	{
		const string source = """
			fun f() -> i32 {
				i32? x = nil
				x ?: ret 42
				0
			}
			""";

		Assert.Equal(KValue.I32(42), Run(source, "f"));
	}

	[Fact]
	public void Wrong_default_type_reports_KRN2102()
	{
		var diagnostic = Assert.Single(
			Diagnostics("""
				fun f() -> str {
					i32? x = nil
					i32 v = x ?: "нет"
					"ok"
				}
				"""),
			item => item.Code == "KRN2102");

		Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
	}

	[Fact]
	public void Elvis_survives_kbc_round_trip()
	{
		const string source = """
			fun f() -> i32 {
				i32? x = nil
				x ?: 21
			}
			""";

		var result = Compile(source);
		KbcModule restored = KbcReader.Read(KbcWriter.Write(result.Module));
		var interpreter = new Interpreter(restored, new NativeResolver(Catalog()));

		Assert.Equal(KValue.I32(21), interpreter.Run("f", []));
	}

	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	private static KValue Run(string source, string function) =>
		new Interpreter(Compile(source).Module, new NativeResolver(Catalog())).Run(function, []);

	private static CodegenResult Compile(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);

		var catalog = Catalog();
		ModuleSymbol imported = NativeSymbolBuilder.Import(module, catalog, out var importDiagnostics);
		Assert.Empty(importDiagnostics);

		var bound = new Binder(imported, table).Bind();
		Assert.DoesNotContain(bound.Diagnostics, diagnostic => diagnostic.IsError);
		var generated = new CodeGenerator().Generate(bound);
		Assert.DoesNotContain(generated.Diagnostics, diagnostic => diagnostic.IsError);
		return generated;
	}

	private static IReadOnlyList<Diagnostic> Diagnostics(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		return new Binder(module, table).Bind().Diagnostics;
	}
}
