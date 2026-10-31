using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Native;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Kern.Vm.Execution;
using Xunit;

namespace Kern.Tests.Execution;

/// <summary>
/// B7: каталог атрибутов (spec §7). Неизвестное имя даёт KRN2307 в сборщике объявлений,
/// <c>#[deprecated]</c> — KRN2306 на каждом использовании, <c>#[must_use]</c> расширяет
/// KRN2305 на отброшенные значения функций и типов. <c>#[inline]</c>/<c>#[no_inline]</c>/
/// <c>#[cfg]</c>/<c>#[test]</c> принимаются без диагностик.
/// </summary>
public sealed class AttributeTests
{
	[Fact]
	public void Unknown_attribute_warns_KRN2307_at_collector()
	{
		var (_, _, diagnostics) = Collect("""#[unknown] fun f() -> i32 { 1 }""");

		var warning = Assert.Single(diagnostics);
		Assert.Equal("KRN2307", warning.Code);
		Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
		Assert.Contains("unknown", warning.Message);
		Assert.Contains("deprecated", warning.Message);
	}

	[Fact]
	public void Deprecated_function_call_warns_KRN2306()
	{
		var diagnostics = Bind("""
			#[deprecated("use g")] fun f() -> i32 { 1 }
			fun main { _ = f() }
			""").Diagnostics;

		var warning = Assert.Single(diagnostics);
		Assert.Equal("KRN2306", warning.Code);
		Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
		Assert.Contains("f", warning.Message);
		Assert.Contains("use g", warning.Message);
	}

	[Fact]
	public void Deprecated_type_construction_warns_KRN2306()
	{
		var diagnostics = Bind("""
			#[deprecated] type Old(i32 x)
			fun main { _ = Old(1) }
			""").Diagnostics;

		var warning = Assert.Single(diagnostics);
		Assert.Equal("KRN2306", warning.Code);
		Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
		Assert.Contains("Old", warning.Message);
	}

	[Fact]
	public void Must_use_function_and_type_warn_KRN2305_when_discarded()
	{
		var fromFunction = Assert.Single(
			Bind("""
				#[must_use] fun f() -> i32 { 1 }
				fun main { f() }
				""").Diagnostics,
			diagnostic => diagnostic.Code == "KRN2305");
		Assert.Equal(DiagnosticSeverity.Warning, fromFunction.Severity);
		Assert.Contains("must_use", fromFunction.Message);
		Assert.Contains("_ =", fromFunction.Message);

		var fromType = Assert.Single(
			Bind("""
				#[must_use] unit Token(i32 v)
				fun main { Token(1) }
				""").Diagnostics,
			diagnostic => diagnostic.Code == "KRN2305");
		Assert.Equal(DiagnosticSeverity.Warning, fromType.Severity);

		// `_ =` — явный отказ от значения: must_use не предупреждает.
		Assert.DoesNotContain(
			Bind("""
				#[must_use] fun f() -> i32 { 1 }
				fun main { _ = f() }
				""").Diagnostics,
			diagnostic => diagnostic.Code == "KRN2305");

		// must_use поверх result даёт ровно одну диагностику, а не две.
		Assert.Single(
			Bind("""
				#[must_use] fun r() -> result<i32, str> { panic("x") }
				fun main { r() }
				""").Diagnostics,
			diagnostic => diagnostic.Code == "KRN2305");

		// Вложенное в `if` без else значение проверяется тем же путём и предупреждает один раз.
		Assert.Single(
			Bind("""
				#[must_use] fun f() -> i32 { 1 }
				fun main { if true { f() } }
				""").Diagnostics,
			diagnostic => diagnostic.Code == "KRN2305");

		// Класс с must_use на объявлении типа: проверка идёт по типу значения.
		Assert.Single(
			Bind("""
				#[must_use] type Box(i32 v)
				fun main { Box(1) }
				""").Diagnostics,
			diagnostic => diagnostic.Code == "KRN2305");
	}

	[Fact]
	public void Known_attributes_are_accepted_without_extra_diagnostics()
	{
		const string source = """
			#[inline] #[no_inline] #[test] #[cfg("x")] fun f() -> i32 { 1 }
			#[must_use] fun g() -> i32 { 1 }
			#[deprecated("old")] fun h() -> i32 { 1 }

			fun main {
				_ = f()
				_ = g()
				_ = h()
			}
			""";

		var (module, table, collectorDiagnostics) = Collect(source);
		Assert.Empty(collectorDiagnostics);
		Assert.DoesNotContain(collectorDiagnostics, diagnostic => diagnostic.Code == "KRN2307");

		ModuleSymbol imported = NativeSymbolBuilder.Import(module, Catalog(), out var importDiagnostics);
		Assert.Empty(importDiagnostics);

		var bound = new Binder(imported, table).Bind();
		var warning = Assert.Single(bound.Diagnostics);
		Assert.Equal("KRN2306", warning.Code);

		var generated = new CodeGenerator().Generate(bound);
		Assert.DoesNotContain(generated.Diagnostics, diagnostic => diagnostic.IsError);
		new Interpreter(generated.Module, new NativeResolver(Catalog())).Run("main", []);
	}

	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	private static (ModuleSymbol Module, SymbolTable Table, IReadOnlyList<Diagnostic> Diagnostics) Collect(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, diagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		return (module, table, diagnostics);
	}

	private static BoundModule Bind(string source)
	{
		var (module, table, diagnostics) = Collect(source);
		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.IsError);
		return new Binder(module, table).Bind();
	}
}
