using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Xunit;

namespace Kern.Tests.Semantics.Binding;

/// <summary>M3: binder декодирует строковые и char-литералы (spec §3, префиксы §22).</summary>
public sealed class BinderLiteralTests
{
	[Fact]
	public void Plain_string_literal_decodes_without_quotes()
	{
		var literal = Literal("""fun f() -> str { "hello" }""");

		Assert.Equal("hello", literal.Value);
		Assert.Equal(PrimitiveType.Str, literal.Type);
	}

	[Fact]
	public void String_escapes_decode()
	{
		var literal = Literal("fun f() -> str { \"a\\n\\t\\u0041\\\\\\\"\" }");

		Assert.Equal("a\n\tA\\\"", literal.Value);
	}

	[Fact]
	public void Raw_string_keeps_backslashes()
	{
		var literal = Literal("fun f() -> str { r\"C:\\temp\\n\" }");

		Assert.Equal("C:\\temp\\n", literal.Value);
	}

	[Fact]
	public void Verbatim_string_doubles_quotes()
	{
		var literal = Literal("fun f() -> str { v\"a\"\"b\" }");

		Assert.Equal("a\"b", literal.Value);
	}

	[Fact]
	public void Raw_triple_string_keeps_content()
	{
		var literal = Literal("fun f() -> str { r\"\"\"line1\\nline2\"\"\" }");

		Assert.Equal("line1\\nline2", literal.Value);
	}

	[Fact]
	public void Char_literal_decodes_plain_and_escaped_forms()
	{
		Assert.Equal('h', Literal("fun f() -> char { 'h' }").Value);
		Assert.Equal('\n', Literal("fun f() -> char { '\\n' }").Value);
		Assert.Equal('\u4564', Literal("fun f() -> char { '\\u4564' }").Value);
	}

	[Fact]
	public void Pattern_literals_decode_too()
	{
		var module = Bind("""
			fun f(str s) -> i32 {
				match s { "a\n" => 1; _ => 0 }
			}
			""");
		var function = module.FindFunction("f")!;
		var match = Assert.IsType<BoundMatchExpression>(function.Body.Value);
		var pattern = Assert.IsType<BoundLiteralPattern>(match.Arms[0].Pattern);

		Assert.Equal("a\n", Assert.IsType<BoundLiteralExpression>(pattern.Value).Value);
	}

	[Fact]
	public void Invalid_string_escape_reports_KRN2101()
	{
		var diagnostics = Diagnostics("fun f() -> str { \"a\\q\" }");

		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2101");
	}

	[Fact]
	public void Interpolated_prefix_binds_now()
	{
		var diagnostics = Diagnostics("fun f() -> str { f\"hi\" }");

		Assert.Empty(diagnostics);
	}

	[Fact]
	public void Regex_prefix_reports_KRN2101_until_implemented()
	{
		var diagnostics = Diagnostics("fun f() -> str { x\"\\d+\" }");

		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2101");
	}

	private static BoundLiteralExpression Literal(string source)
	{
		var function = Bind(source).FindFunction("f")!;
		return Assert.IsType<BoundLiteralExpression>(function.Body.Value);
	}

	private static BoundModule Bind(string source)
	{
		var (module, diagnostics) = BindWithDiagnostics(source);
		Assert.Empty(diagnostics);
		return module;
	}

	private static IReadOnlyList<Diagnostic> Diagnostics(string source) =>
		BindWithDiagnostics(source).Diagnostics;

	private static (BoundModule Module, IReadOnlyList<Diagnostic> Diagnostics) BindWithDiagnostics(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		var bound = new Binder(module, table).Bind();
		return (bound, bound.Diagnostics);
	}
}
