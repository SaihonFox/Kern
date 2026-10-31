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
/// <c>kern.regex</c> (spec §12/§18c): значение <c>regex</c> из <c>regex(шаблон[, флаги])</c>,
/// члены <c>is_match/find/find_all/replace/split</c>, свойства <c>pattern</c>/<c>options</c>,
/// <c>try_new</c> и паника по-русски на неверный шаблон/флаг. Литералы <c>x"…"</c> — отдельный
/// компиляторный срез, поэтому везде явные вызовы <c>regex(…)</c>.
/// </summary>
public sealed class RegexModuleTests
{
	[Fact]
	public void Is_match_reports_presence_and_honours_flags()
	{
		const string source = """
			fun digits() -> bool { regex r = regex(r"\d+")  r.is_match("abc 123") }
			fun letters_only() -> bool { regex r = regex(r"\d+")  r.is_match("abc") }
			fun insensitive() -> bool { regex r = regex("hello", "i")  r.is_match("HELLO") }
			fun sensitive() -> bool { regex r = regex("hello")  r.is_match("HELLO") }
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "digits"));
		Assert.Equal(KValue.Bool(false), Run(source, "letters_only"));
		Assert.Equal(KValue.Bool(true), Run(source, "insensitive"));
		Assert.Equal(KValue.Bool(false), Run(source, "sensitive"));
	}

	[Fact]
	public void Find_returns_the_first_match_or_nil()
	{
		const string source = """
			fun found() -> str? { regex r = regex(r"\d+")  r.find("abc 123 def 456") }
			fun missing() -> str? { regex r = regex(r"\d+")  r.find("abc") }
			fun missing_is_nil() -> bool { regex r = regex(r"\d+")  r.find("abc") == nil }
			fun found_is_not_nil() -> bool { regex r = regex(r"\d+")  r.find("a1") != nil }
			""";

		Assert.Equal(KValue.Str("123"), Run(source, "found"));
		Assert.Equal(KValue.Nil, Run(source, "missing"));
		Assert.Equal(KValue.Bool(true), Run(source, "missing_is_nil"));
		Assert.Equal(KValue.Bool(true), Run(source, "found_is_not_nil"));
	}

	[Fact]
	public void Find_all_and_split_return_lists()
	{
		const string source = """
			fun all_text() -> str { regex r = regex(r"\d+")  r.find_all("a1 b22 c333").join("-") }
			fun all_len() -> i32 { regex r = regex(r"\d+")  r.find_all("a1 b22 c333").len }
			fun none_len() -> i32 { regex r = regex(r"\d+")  r.find_all("abc").len }
			fun split_text() -> str { regex r = regex(",")  r.split("a,b,,c").join("|") }
			fun split_len() -> i32 { regex r = regex(",")  r.split("a,b,,c").len }
			""";

		Assert.Equal(KValue.Str("1-22-333"), Run(source, "all_text"));
		Assert.Equal(KValue.I32(3), Run(source, "all_len"));
		Assert.Equal(KValue.I32(0), Run(source, "none_len"));
		Assert.Equal(KValue.Str("a|b||c"), Run(source, "split_text"));
		Assert.Equal(KValue.I32(4), Run(source, "split_len"));
	}

	[Fact]
	public void Replace_substitutes_every_match_and_supports_groups()
	{
		const string source = """
			fun plain() -> str { regex r = regex(r"\d+")  r.replace("a1 b22 c333", "#") }
			fun groups() -> str { regex r = regex(r"(\w)(\d+)")  r.replace("a1 b22", "$2$1") }
			fun untouched() -> str { regex r = regex(r"\d+")  r.replace("abc", "#") }
			""";

		Assert.Equal(KValue.Str("a# b# c#"), Run(source, "plain"));
		Assert.Equal(KValue.Str("1a 22b"), Run(source, "groups"));
		Assert.Equal(KValue.Str("abc"), Run(source, "untouched"));
	}

	[Fact]
	public void Pattern_and_options_are_properties()
	{
		const string source = """
			fun pattern() -> str { regex r = regex(r"\d+", "mi")  r.pattern }
			fun options() -> str { regex r = regex(r"\d+", "mism")  r.options }
			fun no_options() -> str { regex r = regex(r"\d+")  r.options }
			""";

		Assert.Equal(KValue.Str(@"\d+"), Run(source, "pattern"));
		Assert.Equal(KValue.Str("ims"), Run(source, "options"));
		Assert.Equal(KValue.Str(""), Run(source, "no_options"));
	}

	[Fact]
	public void Invalid_pattern_and_unknown_flag_panic_in_russian()
	{
		const string source = """
			fun bad_pattern() -> bool { regex bad = regex("[a-")  bad.is_match("x") }
			fun bad_flag() -> bool { regex bad = regex("x", "q")  true }
			""";

		var module = Compile(source);
		var interpreter = new Interpreter(module.Module, new NativeResolver(Catalog()));

		var patternError = Assert.Throws<KernPanic>(() => interpreter.Run("bad_pattern"));
		Assert.Contains("неверный шаблон", patternError.Message, StringComparison.Ordinal);

		var flagError = Assert.Throws<KernPanic>(() => interpreter.Run("bad_flag"));
		Assert.Contains("неизвестный флаг", flagError.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Try_new_returns_nil_instead_of_panicking()
	{
		const string source = """
			fun invalid() -> bool { regex? maybe = try_new("(")  maybe == nil }
			fun invalid_flag() -> bool { regex? maybe = try_new(r"\d+", "q")  maybe == nil }
			fun valid() -> bool { regex? maybe = try_new(r"\d+", "i")  maybe != nil }
			fun usable() -> bool {
				regex? maybe = try_new(r"\d+", "i")
				if maybe == nil {
					false
				} else {
					maybe.is_match("A1")
				}
			}
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "invalid"));
		Assert.Equal(KValue.Bool(true), Run(source, "invalid_flag"));
		Assert.Equal(KValue.Bool(true), Run(source, "valid"));
		Assert.Equal(KValue.Bool(true), Run(source, "usable"));
	}

	[Fact]
	public void Catalog_exposes_the_regex_type_and_nullable_signatures()
	{
		NativeCatalog catalog = Catalog();

		NativeFunctionDescriptor factory = Assert.Single(
			catalog.Functions,
			function => function.Owner == "kern.regex" && function.Name == "regex" && function.Parameters.Count == 1);
		Assert.Equal("regex", factory.ReturnType);
		Assert.Equal("str", Assert.Single(factory.Parameters).Type);

		NativeFunctionDescriptor tryNew = Assert.Single(
			catalog.Functions,
			function => function.Owner == "kern.regex" && function.Name == "try_new" && function.Parameters.Count == 1);
		Assert.Equal("regex?", tryNew.ReturnType);

		NativeFunctionDescriptor find = Assert.Single(
			catalog.Functions,
			function => function.Owner == "regex" && function.Name == "find");
		Assert.Equal("regex", find.SelfType);
		Assert.Equal("str?", find.ReturnType);

		NativeFunctionDescriptor findAll = Assert.Single(
			catalog.Functions,
			function => function.Owner == "regex" && function.Name == "find_all");
		Assert.Equal("list<str>", findAll.ReturnType);

		Assert.Contains(catalog.Types, type => type.Name == "regex");
		Assert.Empty(catalog.Diagnostics);
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
