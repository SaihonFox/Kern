using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Bytecode;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Native;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Execution;

/// <summary>
/// B8c: <c>#[derive(clone)]</c> (spec §7/§16). У <c>type</c> derive включает неглубокую
/// копию объекта: поля-значения (<c>unit</c>/<c>unit!</c>) копируются рекурсивно, ссылки
/// (<c>type</c>, коллекции, замыкания) остаются общими; клон — новый экземпляр, поэтому
/// <c>==</c>/<c>!=</c> по идентичности его отличают. Ручной <c>op clone()</c> или метод
/// <c>clone()</c> конфликтует с derive (KRN2106); у <c>type</c> без derive и без ручного
/// оператора — KRN2101. У <c>unit</c>/<c>unit!</c> значение копируется само, поэтому
/// <c>clone()</c> возвращает получателя, а derive — принятый no-op.
/// </summary>
public sealed class CloneDeriveTests
{
	[Fact]
	public void Clone_returns_distinct_object_with_equal_fields()
	{
		const string source = """
			#[derive(clone)] type Point(i32 x, i32 y)

			fun distinct() -> bool {
				Point a = Point(1, 2)
				a.clone() != a
			}

			fun equal_fields() -> bool {
				Point a = Point(1, 2)
				Point b = a.clone()
				b.x == a.x && b.y == a.y
			}
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "distinct"));
		Assert.Equal(KValue.Bool(true), Run(source, "equal_fields"));
	}

	[Fact]
	public void Mutating_the_clone_leaves_the_original_unchanged()
	{
		const string source = """
			#[derive(clone)] type Point(i32 x, i32 y)

			fun f() -> i32 {
				Point a = Point(1, 2)
				Point b = a.clone()
				b.x = 9
				a.x * 10 + b.x
			}
			""";

		Assert.Equal(KValue.I32(19), Run(source, "f"));
	}

	[Fact]
	public void Clone_copies_unit_fields_but_shares_class_references()
	{
		// inner — unit-поле (значение): запись через клон идёт copy-out/copy-back и
		// не трогает оригинал. part — type-поле (ссылка): клон и оригинал видят один
		// и тот же экземпляр, поэтому запись через клон меняет оригинал.
		const string source = """
			unit Inner(i32 v)
			type Part(i32 v)
			#[derive(clone)] type Holder(Inner inner, Part part)

			fun unit_field_is_copied() -> i32 {
				Holder a = Holder(Inner(1), Part(5))
				Holder b = a.clone()
				b.inner.v = 9
				a.inner.v
			}

			fun class_field_is_shared() -> i32 {
				Holder a = Holder(Inner(1), Part(5))
				Holder b = a.clone()
				b.part.v = 9
				a.part.v
			}
			""";

		Assert.Equal(KValue.I32(1), Run(source, "unit_field_is_copied"));
		Assert.Equal(KValue.I32(9), Run(source, "class_field_is_shared"));
	}

	[Fact]
	public void Clone_requires_derive_or_a_manual_operator()
	{
		var diagnostic = Assert.Single(
			Diagnostics("""
				type Point(i32 x, i32 y)

				fun f() -> Point { Point(1, 2).clone() }
				"""),
			item => item.Code == "KRN2101");

		Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
		Assert.Contains("clone", diagnostic.Message);
		Assert.Contains("derive", diagnostic.Message);

		const string manual = """
			type Point(i32 x, i32 y) {
				op clone() -> Point { Point($x, $y) }
			}

			fun distinct() -> bool { Point(1, 2).clone() != Point(1, 2) }

			fun equal_fields() -> bool {
				Point p = Point(1, 2)
				Point q = p.clone()
				q.x == 1 && q.y == 2
			}
			""";

		Assert.Equal(KValue.Bool(true), Run(manual, "distinct"));
		Assert.Equal(KValue.Bool(true), Run(manual, "equal_fields"));
	}

	[Fact]
	public void Unit_derive_clone_is_a_no_op()
	{
		const string source = """
			unit Plain(i32 x)
			#[derive(clone)] unit Derived(i32 x)
			#[derive(clone)] unit! Frozen(i32 x)

			fun plain() -> i32 { Plain(1).clone().x }
			fun derived() -> i32 { Derived(2).clone().x }
			fun frozen() -> i32 { Frozen(3).clone().x }

			fun structural_equality() -> bool {
				Plain p = Plain(4)
				p.clone() == p
			}
			""";

		Assert.Equal(KValue.I32(1), Run(source, "plain"));
		Assert.Equal(KValue.I32(2), Run(source, "derived"));
		Assert.Equal(KValue.I32(3), Run(source, "frozen"));
		Assert.Equal(KValue.Bool(true), Run(source, "structural_equality"));
	}

	[Fact]
	public void Manual_clone_members_conflict_with_derive()
	{
		const string operatorSource = """
			#[derive(clone)] type Point(i32 x) {
				op clone() -> Point { Point($x) }
			}
			""";

		var operatorDiagnostic = Assert.Single(
			Diagnostics(operatorSource),
			item => item.Code == "KRN2106");

		Assert.Equal(DiagnosticSeverity.Error, operatorDiagnostic.Severity);
		Assert.Contains("clone", operatorDiagnostic.Message);
		Assert.Contains("derive", operatorDiagnostic.Message);
		Assert.Equal(operatorSource.IndexOf("#[derive", StringComparison.Ordinal), operatorDiagnostic.Span.Start);

		const string methodSource = """
			#[derive(clone)] type Point(i32 x) {
				fun clone() -> Point { Point($x) }
			}
			""";

		var methodDiagnostic = Assert.Single(
			Diagnostics(methodSource),
			item => item.Code == "KRN2106");

		Assert.Equal(DiagnosticSeverity.Error, methodDiagnostic.Severity);
		Assert.Contains("clone", methodDiagnostic.Message);
		Assert.Contains("derive", methodDiagnostic.Message);
		Assert.Equal(methodSource.IndexOf("#[derive", StringComparison.Ordinal), methodDiagnostic.Span.Start);
	}

	[Fact]
	public void Derived_clone_survives_kbc_round_trip()
	{
		const string source = """
			#[derive(clone)] type Point(i32 x, i32 y)

			fun distinct() -> bool {
				Point a = Point(1, 2)
				a.clone() != a
			}

			fun equal_fields() -> bool {
				Point a = Point(1, 2)
				Point b = a.clone()
				b.x == 1 && b.y == 2
			}
			""";

		var result = Compile(source);
		KbcModule restored = KbcReader.Read(KbcWriter.Write(result.Module));
		var interpreter = new Interpreter(restored, new NativeResolver(Catalog()));

		Assert.Contains((int)OpCode.CloneObject, restored.FunctionPool.SelectMany(function => function.Code));
		Assert.Equal(KValue.Bool(true), interpreter.Run("distinct", []));
		Assert.Equal(KValue.Bool(true), interpreter.Run("equal_fields", []));
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

	private static BoundModule Bind(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		return new Binder(module, table).Bind();
	}

	private static IReadOnlyList<Diagnostic> Diagnostics(string source) => Bind(source).Diagnostics;
}
