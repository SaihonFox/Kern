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
/// <c>kern.json</c> (spec §16): <c>to_str</c>/<c>to_str_pretty</c>/<c>parse</c> и
/// <c>from_str&lt;T&gt;</c> с reified-аргументом типа (<c>[KernTypeArgs]</c>).
/// </summary>
public sealed class JsonModuleTests
{
	[Fact]
	public void Unit_round_trips_through_json()
	{
		const string source = """
			unit Person(str name, i32 age)

			fun text() -> str { json.to_str(Person("аня", 25)) }
			fun round_trip() -> str {
				match json.from_str<Person>(json.to_str(Person("аня", 25))) {
					Ok(back) => f"${back.name}/${back.age}"
					Err(error) => f"ошибка: ${error}"
				}
			}
			fun invalid() -> bool {
				match json.from_str<Person>("{не json") {
					Ok(_) => false
					Err(e) => e.len > 0
				}
			}
			""";

		Assert.Equal(KValue.Str("""{"name":"аня","age":25}"""), Run(source, "text"));
		Assert.Equal(KValue.Str("аня/25"), Run(source, "round_trip"));
		Assert.Equal(KValue.Bool(true), Run(source, "invalid"));
	}

	[Fact]
	public void Parse_returns_dynamic_structures()
	{
		const string source = """
			fun object_text() -> str {
				match json.parse("{\"x\": [1, 2, 3]}") {
					Ok(value) => json.to_str(value)
					Err(_) => "error"
				}
			}
			fun array_text() -> str {
				match json.parse("[1, 2]") {
					Ok(value) => json.to_str(value)
					Err(_) => "error"
				}
			}
			fun broken() -> bool {
				match json.parse("{") {
					Ok(_) => false
					Err(e) => e.len > 0
				}
			}
			""";

		Assert.Equal(KValue.Str("""{"x":[1,2,3]}"""), Run(source, "object_text"));
		Assert.Equal(KValue.Str("[1,2]"), Run(source, "array_text"));
		Assert.Equal(KValue.Bool(true), Run(source, "broken"));
	}

	[Fact]
	public void Collections_and_primitives_serialize()
	{
		const string source = """
			fun list_text() -> str { json.to_str(<i32>[1, 2, 3]) }
			fun dict_text() -> str { json.to_str(<str: i32>["a": 1]) }
			fun bool_text() -> str { json.to_str(true) }
			fun string_text() -> str { json.to_str("abc") }
			""";

		Assert.Equal(KValue.Str("[1,2,3]"), Run(source, "list_text"));
		Assert.Equal(KValue.Str("""{"a":1}"""), Run(source, "dict_text"));
		Assert.Equal(KValue.Str("true"), Run(source, "bool_text"));
		Assert.Equal(KValue.Str("\"abc\""), Run(source, "string_text"));
	}

	[Fact]
	public void Pretty_output_is_indented()
	{
		const string source = """
			fun pretty() -> str { json.to_str_pretty(<i32>[1]) }
			""";

		KValue value = Run(source, "pretty");
		Assert.Equal(ValueKind.Str, value.Kind);
		Assert.Contains('\n', value.AsStr);
		Assert.Contains("1", value.AsStr);
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
