using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Проверка целей и аргументов встроенных атрибутов (spec §7): недопустимая цель или форма
/// аргументов — <c>KRN2101</c>; неизвестное имя по-прежнему только <c>KRN2307</c>.
/// </summary>
public sealed class AttributeValidationTests
{
	[Fact]
	public void Valid_targets_and_arguments_are_accepted()
	{
		var diagnostics = Collect("""
			#[module(author = "kern", version = "1.0")]
			project demo.attributes:

			#[deprecated("старое"), must_use]
			type Old(i32 x) {
				#[cfg("поле")] i32 marked
				#[must_use] i32 value => 1
				#[deprecated] fun helper() -> i32 { 1 }
			}

			#[inline] fun fast(i32 x) -> i32 x
			#[memo] fun cached(i32 x) -> i32 x
			#[test] fun check() {}
			#[macro("expr")] fun mac() -> str "x"

			#[controller("/api")]
			type api {
				#[get("/")]
				#[auth]
				#[roles("admin", "user")]
				fun index() -> i32 1
			}

			#[singleton]
			type service {
				#[inject] i32 first
				#[inject("name")] i32 second
				#[inject(optional: true)] i32 third
			}

			#[needs(net, fs.read("data/"), fs.write("out/"), process, db, env)]
			fun main() -> i32 0
			""");

		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "KRN2101");
		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "KRN2307");
	}

	[Theory]
	[InlineData("""#[inline] type Bad {}""")]
	[InlineData("""#[derive(to_str)] fun f() -> i32 1""")]
	[InlineData("""#[inject] fun f() {}""")]
	[InlineData("""#[controller] fun f() {}""")]
	[InlineData("""#[test] type Bad {}""")]
	[InlineData("""#[get] type Bad {}""")]
	[InlineData("""#[singleton] unit Bad(i32 x)""")]
	[InlineData("""#[roles("admin")] unit Bad(i32 x)""")]
	public void Attribute_on_a_wrong_target_reports_KRN2101(string source)
	{
		var diagnostics = Collect(source);

		var error = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "KRN2101");
		Assert.Contains("атрибут допустим только", error.Message);
	}

	[Fact]
	public void Misplaced_module_attribute_reports_KRN2101()
	{
		var diagnostics = Collect("""
			fun first() -> i32 1
			#[module(author = "kern")]
			fun second() -> i32 2
			""");

		var error = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "KRN2101");
		Assert.Contains("только в начале файла", error.Message);
	}

	[Theory]
	[InlineData("""#[deprecated(1)] fun f() {}""")]
	[InlineData("""#[deprecated("a", "b")] fun f() {}""")]
	[InlineData("""#[must_use("x")] fun f() {}""")]
	[InlineData("""#[cfg(1)] fun f() {}""")]
	[InlineData("""#[roles] fun f() {}""")]
	[InlineData("""#[roles(1)] fun f() {}""")]
	[InlineData("""#[allow] fun f() {}""")]
	[InlineData("""#[allow(KRN1)] fun f() {}""")]
	[InlineData("""#[get(1)] fun f() {}""")]
	[InlineData("""#[macro(1)] fun f() {}""")]
	[InlineData("""#[auth("x")] fun f() {}""")]
	[InlineData("""#[needs(bogus)] fun f() {}""")]
	[InlineData("""#[needs(fs.read(1))] fun f() {}""")]
	[InlineData("""#[module(author)] fun f() {}""")]
	[InlineData("""#[controller(1)] type Bad {}""")]
	[InlineData("""#[singleton("x")] type Bad {}""")]
	public void Invalid_attribute_arguments_report_KRN2101(string source)
	{
		var diagnostics = Collect(source);

		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2101");
	}

	[Fact]
	public void Invalid_inject_arguments_report_KRN2101()
	{
		var diagnostics = Collect("""
			type Holder {
				#[inject(optional: 1)] i32 value
			}
			""");

		var error = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "KRN2101");
		Assert.Contains("#[inject]", error.Message);
	}

	[Fact]
	public void Invalid_if_condition_still_reports_KRN2101()
	{
		var diagnostics = Collect("""#[if(os = "solaris")] fun f() -> i32 1""");

		var error = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "KRN2101");
		Assert.Contains("solaris", error.Message);
	}

	private static IReadOnlyList<Diagnostic> Collect(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (_, diagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		return diagnostics;
	}
}
