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
/// Интерполяция строк <c>f"…"</c> (spec §3): <c>${expr}</c>, <c>${expr=}</c> и спецификации
/// <c>:&lt;10</c>, <c>:08d</c>, <c>:.2f</c>; <c>fr"…"</c>/<c>fv"…"</c> комбинируются с
/// интерполяцией, <c>\${</c> остаётся текстом, пустое <c>${}</c> — ошибка KRN2101.
/// </summary>
public sealed class InterpolationTests
{
	[Fact]
	public void Plain_and_multiple_parts()
	{
		Assert.Equal(KValue.Str("a3b"), Run("""fun f() -> str { f"a${1 + 2}b" }""", "f"));
		Assert.Equal(KValue.Str("5-5"), Run("""fun f() -> str { f"${2 + 3}-${10 / 2}" }""", "f"));
	}

	[Fact]
	public void Interpolation_of_fields_and_calls()
	{
		const string source = """
			unit Point(i32 x, i32 y)

			fun sum(i32 a) -> i32 { a + 1 }

			fun f() -> str {
				Point p = Point(1, 2)
				f"${p.x}:${p.y}:${sum(41)}"
			}
			""";

		Assert.Equal(KValue.Str("1:2:42"), Run(source, "f"));
	}

	[Fact]
	public void Debug_form_prints_the_expression_text()
	{
		Assert.Equal(KValue.Str("1 + 2=3"), Run("""fun f() -> str { f"${1 + 2=}" }""", "f"));
		Assert.Equal(KValue.Str("x=" + "7"), Run("""
			fun f() -> str {
				i32 x = 7
				f"${x=}"
			}
			""", "f"));
	}

	[Fact]
	public void Format_specs_fixed_width_and_zero_padding()
	{
		Assert.Equal(KValue.Str("3.14"), Run("""fun f() -> str { f"${3.14159:.2f}" }""", "f"));
		Assert.Equal(KValue.Str("7   |"), Run("""fun f() -> str { f"${7:<4}|" }""", "f"));
		Assert.Equal(KValue.Str("   7|"), Run("""fun f() -> str { f"${7:>4}|" }""", "f"));
		Assert.Equal(KValue.Str("00000042"), Run("""fun f() -> str { f"${42:08d}" }""", "f"));
	}

	[Fact]
	public void Escaped_dollar_stays_text()
	{
		Assert.Equal(KValue.Str("${1}"), Run("""fun f() -> str { f"\${1}" }""", "f"));
	}

	[Fact]
	public void Raw_and_verbatim_prefixes_still_interpolate()
	{
		Assert.Equal(KValue.Str("a\\b1"), Run("""fun f() -> str { fr"a\b${1}" }""", "f"));
		Assert.Equal(KValue.Str("a\"b2"), Run("""fun f() -> str { fv"a""b${2}" }""", "f"));
	}

	[Fact]
	public void Empty_interpolation_reports_KRN2101()
	{
		var diagnostic = Assert.Single(
			Diagnostics("""fun f() -> str { f"${}" }"""),
			item => item.Code == "KRN2101");

		Assert.Contains("интерполяц", diagnostic.Message);
	}

	[Fact]
	public void Interpolation_survives_kbc_round_trip()
	{
		const string source = """
			unit Point(i32 x, i32 y)

			fun f() -> str {
				Point p = Point(1, 2)
				f"p=${p.x=}; fixed=${3.5:.2f}; padded=${42:06d}"
			}
			""";

		var result = Compile(source);
		KbcModule restored = KbcReader.Read(KbcWriter.Write(result.Module));
		var interpreter = new Interpreter(restored, new NativeResolver(Catalog()));

		Assert.Equal(
			KValue.Str("p=p.x=1; fixed=3.50; padded=000042"),
			interpreter.Run("f", []));
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
