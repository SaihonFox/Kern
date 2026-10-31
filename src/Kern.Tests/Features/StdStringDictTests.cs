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
/// Числовой разбор `str` (`to_i32`/`to_i64`/`to_f64`/`to_bool`) и `dict.get_or`
/// (spec §12): нативные члены без изменения VM, `nil` — через `match`.
/// </summary>
public sealed class StdStringDictTests
{
	private const string Source = """
		fun parse_ok() -> i32 {
			i32 n = "42".to_i32() ?: -1
			n
		}

		fun parse_bad_fallback() -> i32 {
			"abc".to_i32() ?: -1
		}

		fun parse_bad_is_nil() -> bool
			match "abc".to_i32() {
				nil => true
				_ => false
			}

		fun parse_negative() -> i32 {
			"-7".to_i32() ?: 0
		}

		fun parse_i32_overflow_is_nil() -> bool
			match "2147483648".to_i32() {
				nil => true
				_ => false
			}

		fun parse_i64_large() -> i64 {
			"9000000000".to_i64() ?: 0
		}

		fun parse_i64_bad_is_nil() -> bool
			match "x".to_i64() {
				nil => true
				_ => false
			}

		fun parse_f64_decimal() -> f64 {
			"2.5".to_f64() ?: 0.0
		}

		fun parse_f64_exponent() -> f64 {
			"1e3".to_f64() ?: 0.0
		}

		fun parse_f64_bad_is_nil() -> bool
			match "x".to_f64() {
				nil => true
				_ => false
			}

		fun parse_bool_true() -> bool {
			"true".to_bool() ?: false
		}

		fun parse_bool_false() -> bool {
			"false".to_bool() ?: true
		}

		fun parse_bool_rejects_yes() -> bool
			match "yes".to_bool() {
				nil => true
				_ => false
			}

		fun parse_bool_is_ordinal() -> bool
			match "TRUE".to_bool() {
				nil => true
				_ => false
			}

		fun dict_get_or_existing() -> i32 {
			dict<str, i32> stock = ["a": 1]
			stock.get_or("a", -1)
		}

		fun dict_get_or_missing() -> i32 {
			dict<str, i32> stock = ["a": 1]
			stock.get_or("b", -1)
		}

		fun dict_get_or_len_unchanged() -> i32 {
			dict<str, i32> stock = ["a": 1]
			i32 before = stock.len
			stock.get_or("b", 0)
			stock.len - before
		}

		fun dict_get_or_does_not_insert() -> bool {
			dict<str, i32> stock = ["a": 1]
			stock.get_or("b", -1)
			stock.contains_key("b")
		}
		""";

	[Fact]
	public void String_to_i32_parses_with_elvis_and_falls_back()
	{
		Assert.Equal(KValue.I32(42), Run("parse_ok"));
		Assert.Equal(KValue.I32(-1), Run("parse_bad_fallback"));
		Assert.Equal(KValue.I32(-7), Run("parse_negative"));
	}

	[Fact]
	public void String_to_i32_returns_nil_on_failure_and_overflow()
	{
		Assert.Equal(KValue.Bool(true), Run("parse_bad_is_nil"));
		Assert.Equal(KValue.Bool(true), Run("parse_i32_overflow_is_nil"));
	}

	[Fact]
	public void String_to_i64_parses_large_values_and_returns_nil()
	{
		Assert.Equal(KValue.I64(9_000_000_000L), Run("parse_i64_large"));
		Assert.Equal(KValue.Bool(true), Run("parse_i64_bad_is_nil"));
	}

	[Fact]
	public void String_to_f64_parses_decimal_and_exponent()
	{
		Assert.Equal(KValue.F64(2.5), Run("parse_f64_decimal"));
		Assert.Equal(KValue.F64(1000.0), Run("parse_f64_exponent"));
		Assert.Equal(KValue.Bool(true), Run("parse_f64_bad_is_nil"));
	}

	[Fact]
	public void String_to_bool_accepts_only_true_and_false()
	{
		Assert.Equal(KValue.Bool(true), Run("parse_bool_true"));
		Assert.Equal(KValue.Bool(false), Run("parse_bool_false"));
		Assert.Equal(KValue.Bool(true), Run("parse_bool_rejects_yes"));
		Assert.Equal(KValue.Bool(true), Run("parse_bool_is_ordinal"));
	}

	[Fact]
	public void Dict_get_or_returns_fallback_without_inserting()
	{
		Assert.Equal(KValue.I32(1), Run("dict_get_or_existing"));
		Assert.Equal(KValue.I32(-1), Run("dict_get_or_missing"));
		Assert.Equal(KValue.I32(0), Run("dict_get_or_len_unchanged"));
		Assert.Equal(KValue.Bool(false), Run("dict_get_or_does_not_insert"));
	}

	private sealed record Outcome(CodegenResult? Code, IReadOnlyList<Diagnostic> Diagnostics);

	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	private static KValue Run(string function) =>
		new Interpreter(Compile(Source).Module, new NativeResolver(Catalog())).Run(function, []);

	private static CodegenResult Compile(string source)
	{
		var outcome = CompileWithDiagnostics(source);
		Assert.DoesNotContain(outcome.Diagnostics, diagnostic => diagnostic.IsError);
		Assert.NotNull(outcome.Code);
		return outcome.Code!;
	}

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
