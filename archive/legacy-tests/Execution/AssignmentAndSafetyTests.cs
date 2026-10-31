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

namespace Kern.Tests.Execution;

/// <summary>
/// Мелкие языковые срезы, нужные примеру 01: swap/многоцелевое присваивание, безопасный
/// доступ <c>?.</c> (spec §3) и константные преобразования литералов (<c>u8 small = 5</c>).
/// </summary>
public sealed class AssignmentAndSafetyTests
{
	[Fact]
	public void Swap_uses_values_before_storing()
	{
		Assert.Equal(KValue.I32(21), Run("""
			fun f() -> i32 {
				i32 a = 1
				i32 b = 2
				a, b = b, a
				a * 10 + b
			}
			""", "f"));
	}

	[Fact]
	public void Three_targets_and_declared_types_are_converted()
	{
		Assert.Equal(KValue.I32(312), Run("""
			fun f() -> i32 {
				i32 a = 1
				i32 b = 2
				i32 c = 3
				a, b, c = c, a, b
				a * 100 + b * 10 + c
			}
			""", "f"));
	}

	[Fact]
	public void Mismatched_target_count_reports_KRN2102()
	{
		var diagnostic = Assert.Single(
			Diagnostics("""
				fun f {
					i32 a = 1
					i32 b = 2
					a, b = b
				}
				"""),
			item => item.Code == "KRN2102");

		Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
	}

	[Fact]
	public void Conditional_call_returns_nil_for_nil_receiver()
	{
		Assert.Equal(KValue.Str("пусто"), Run("""
			fun f() -> str {
				str? s = nil
				s?.to_str() ?: "пусто"
			}
			""", "f"));
		Assert.Equal(KValue.Str("ok"), Run("""
			fun f() -> str {
				str? s = "ok"
				s?.to_str() ?: "пусто"
			}
			""", "f"));
	}

	[Fact]
	public void Conditional_property_returns_nil_for_nil_receiver()
	{
		Assert.Equal(KValue.I32(-1), Run("""
			fun f() -> i32 {
				str? s = nil
				s?.len ?: -1
			}
			""", "f"));
		Assert.Equal(KValue.I32(4), Run("""
			fun f() -> i32 {
				str? s = "abcd"
				s?.len ?: -1
			}
			""", "f"));
	}

	[Fact]
	public void Conditional_access_evaluates_the_receiver_once_and_skips_the_call()
	{
		Assert.Equal(KValue.I32(100), Run("""
			type Counter {
				#i32 calls = 0
			}

			fun maybe() -> str? {
				Counter.calls = Counter.calls + 1
				nil
			}

			fun f() -> i32 {
				i32 value = maybe()?.len ?: -1
				Counter.calls * 100 + value + 1
			}
			""", "f"));
	}

	[Fact]
	public void Constant_literal_narrows_to_u8()
	{
		KValue value = Run("""
			fun f() -> u8 {
				u8 small = 5
				small
			}
			""", "f");

		Assert.Equal(ValueKind.U8, value.Kind);
		Assert.Equal((byte)5, value.AsU8);
	}

	[Fact]
	public void Out_of_range_constant_reports_KRN2102()
	{
		var diagnostic = Assert.Single(
			Diagnostics("""
				fun f {
					u8 small = 300
				}
				"""),
			item => item.Code == "KRN2102");

		Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
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
