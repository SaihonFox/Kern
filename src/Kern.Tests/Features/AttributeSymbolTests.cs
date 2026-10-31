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
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// M3: атрибуты живут на символах (type, unit, функция/метод/accessor/оператор, поле,
/// свойство), переносятся в <c>.kbc</c> (<c>TypeDef</c>/<c>FunctionDef</c>/<c>FieldDef</c>)
/// и печатаются дизассемблером. Диагностики <c>KRN2306</c>/<c>KRN2305</c>/<c>KRN2307</c>
/// читают те же данные и не дублируются; групповой синтаксис <c>#[A, B]</c> раскрывается
/// в два атрибута (spec §7).
/// </summary>
public sealed class AttributeSymbolTests
{
	[Fact]
	public void Attributes_are_stored_on_type_function_field_and_property_symbols()
	{
		var (module, diagnostics) = Collect("""
			#[deprecated("старое")]
			type Old(i32 x) {
				#[cfg("поле")] i32 marked
				#[must_use] i32 value => 1
			}

			#[must_use]
			unit Marker(i32 x)

			#[inline]
			fun make() -> i32 { 1 }
			""");

		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "KRN2307");

		var type = module.Types["Old"];
		var typeAttribute = Assert.Single(type.Attributes);
		Assert.Equal("deprecated", typeAttribute.Name);
		Assert.Equal(KConst.Str("старое"), Assert.Single(typeAttribute.Args));

		var unit = module.Types["Marker"];
		Assert.Equal("must_use", Assert.Single(unit.Attributes).Name);

		var field = Assert.Single(type.Members.Fields, item => item.Name == "marked");
		Assert.Equal("cfg", Assert.Single(field.Attributes).Name);
		Assert.Equal(KConst.Str("поле"), Assert.Single(field.Attributes[0].Args));

		var property = Assert.Single(type.Members.Properties);
		Assert.Equal("must_use", Assert.Single(property.Attributes).Name);

		var function = Assert.Single(module.Functions["make"]);
		Assert.Equal("inline", Assert.Single(function.Attributes).Name);
	}

	[Fact]
	public void Attribute_arguments_become_typed_constants_and_identifiers_stay_strings()
	{
		var (module, _) = Collect("""
			#[cfg(1, 2.5, true, "текст", 'c', derive)]
			fun marked() -> i32 { 1 }
			""");

		var attribute = Assert.Single(Assert.Single(module.Functions["marked"]).Attributes);
		Assert.Equal(
			new[]
			{
				KConst.I64(1), KConst.F64(2.5), KConst.Bool(true), KConst.Str("текст"), KConst.Char('c'),
				KConst.Str("derive")
			},
			attribute.Args);
	}

	[Fact]
	public void Type_function_and_field_attributes_round_trip_through_kbc()
	{
		var compiled = Compile("""
			#[deprecated("старое")]
			type Old(i32 x) {
				#[cfg("поле")] i32 marked
				#[cfg("статика")] #i32 counter
				#[deprecated] fun helper() -> i32 { 1 }
				#[must_use] i32 value => 1
			}

			#[must_use]
			fun make() -> Old { Old(1) }
			""");

		var stored = KbcReader.Read(KbcWriter.Write(compiled.Module));

		var type = Assert.Single(stored.TypePool, item => item.Name == "Old");
		var typeAttribute = Assert.Single(type.Attributes);
		Assert.Equal("deprecated", typeAttribute.Name);
		Assert.Equal(KConst.Str("старое"), Assert.Single(typeAttribute.Args));

		var field = Assert.Single(type.Fields, item => item.Name == "marked");
		Assert.Equal("cfg", Assert.Single(field.Attributes).Name);
		Assert.Equal(KConst.Str("поле"), Assert.Single(field.Attributes[0].Args));

		var staticField = Assert.Single(stored.FieldPool, item => item.Name == "counter");
		Assert.Equal("cfg", Assert.Single(staticField.Attributes).Name);

		var function = Assert.Single(stored.FunctionPool, item => item.Name == "make");
		Assert.Equal("must_use", Assert.Single(function.Attributes).Name);

		var method = Assert.Single(stored.FunctionPool, item => item.Name == "helper");
		Assert.Equal("deprecated", Assert.Single(method.Attributes).Name);

		var accessor = Assert.Single(stored.FunctionPool, item => item.Name == "value");
		Assert.Equal("must_use", Assert.Single(accessor.Attributes).Name);
	}

	[Fact]
	public void Disassembler_prints_type_and_function_attributes()
	{
		var compiled = Compile("""
			#[deprecated("старое")]
			unit Old(i32 x)

			#[must_use]
			fun make() -> i32 { 1 }
			""");

		var text = Disassembler.Disassemble(compiled.Module);
		Assert.Contains("attr deprecated", text, StringComparison.Ordinal);
		Assert.Contains("attr must_use", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Deprecated_and_must_use_read_symbol_attributes_and_warn_once()
	{
		var diagnostics = CompileWithDiagnostics("""
			#[deprecated("use g")]
			fun old() -> i32 { 1 }

			#[must_use]
			fun keep() -> i32 { 1 }

			fun main {
				_ = old()
				keep()
			}
			""").Diagnostics;

		var deprecated = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "KRN2306");
		Assert.Contains("old", deprecated.Message);
		Assert.Contains("use g", deprecated.Message);

		var required = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "KRN2305");
		Assert.Contains("must_use", required.Message);
	}

	[Fact]
	public void Grouped_attributes_are_split_into_independent_attributes()
	{
		var (module, diagnostics) = Collect("""
			#[deprecated, must_use]
			fun old() -> i32 { 1 }

			fun main { old() }
			""");

		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "KRN2307");
		var attributes = Assert.Single(module.Functions["old"]).Attributes;
		Assert.Equal(new[] { "deprecated", "must_use" }, attributes.Select(attribute => attribute.Name));

		var codes = BindDiagnosticCodes("""
			#[deprecated, must_use]
			fun old() -> i32 { 1 }

			fun main { old() }
			""");
		Assert.Equal(new[] { "KRN2306", "KRN2305" }, codes);
	}

	[Fact]
	public void Unknown_attribute_still_warns_KRN2307_once()
	{
		var (_, diagnostics) = Collect("""
			#[nonsense]
			fun f() -> i32 { 1 }
			""");

		var warning = Assert.Single(diagnostics);
		Assert.Equal("KRN2307", warning.Code);
		Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
	}

	private sealed record Outcome(CodegenResult? Code, IReadOnlyList<Diagnostic> Diagnostics);

	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	private static (ModuleSymbol Module, IReadOnlyList<Diagnostic> Diagnostics) Collect(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		return DeclarationCollector.Collect(parsed.Tree, table, "main");
	}

	private static string[] BindDiagnosticCodes(string source)
	{
		var (module, diagnostics) = Collect(source);
		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.IsError);
		var parsed = Parser.Parse(source);
		var (table, _) = SymbolTable.Collect(parsed.Tree);
		var bound = new Binder(module, table).Bind();
		return [.. bound.Diagnostics.Select(diagnostic => diagnostic.Code)];
	}

	private static CodegenResult Compile(string source)
	{
		var outcome = CompileWithDiagnostics(source);
		Assert.DoesNotContain(outcome.Diagnostics, diagnostic => diagnostic.IsError);
		Assert.NotNull(outcome.Code);
		return outcome.Code!;
	}

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

		ModuleSymbol imported = NativeSymbolBuilder.Import(module, Catalog(), out var importProblems);
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
