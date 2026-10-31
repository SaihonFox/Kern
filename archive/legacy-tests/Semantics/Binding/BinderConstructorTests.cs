using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Xunit;

namespace Kern.Tests.Semantics.Binding;

/// <summary>
/// M3-D: разрешение цепочек конструкторов <c>: $(...)</c> и обязательность цепочки
/// при первичном конструкторе (spec §8, п.829).
/// </summary>
public sealed class BinderConstructorTests
{
	[Fact]
	public void Chain_resolves_to_another_constructor_and_sees_this_parameters()
	{
		var module = Bind("""
			type P(i32 x, i32 y) {
				ctor(i32 x) : $(x, 0) { }
			}
			""");
		var type = module.Symbol.FindType("P")!;
		var constructor = Assert.Single(module.Functions, function =>
			function.Symbol.Kind == FunctionKind.Constructor &&
			!function.Symbol.IsPrimaryConstructor);

		var chain = Assert.IsType<BoundConstructorChain>(constructor.Chain);
		Assert.Same(type.PrimaryConstructor, chain.Target);
		Assert.Equal(2, chain.Arguments.Count);
		Assert.Equal(PrimitiveType.I32, chain.Arguments[0].Type);

		// Аргумент `x` — параметр именно этого конструктора (slot 1 после `$`), а не поля.
		var argument = Assert.IsType<BoundVariableExpression>(chain.Arguments[0]);
		Assert.Equal("x", argument.Name);
		Assert.Equal(1, argument.Slot);

		// Инициализаторы выполняет вызванный конструктор — пролог цепочки пуст.
		Assert.Empty(constructor.Initializers);
	}

	[Fact]
	public void Secondary_constructor_without_chain_reports_KRN2190()
	{
		var diagnostics = Diagnostics("""
			type P(i32 x) {
				ctor(str s) { }
			}
			""");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN2190", diagnostic.Code);
		Assert.Contains("P", diagnostic.Message);
		Assert.Contains(": $(...)", diagnostic.Message);
		Assert.NotEqual(default(TextSpan), diagnostic.Span);
	}

	[Fact]
	public void Each_offending_constructor_reports_its_own_KRN2190()
	{
		const string source =
			"""
			type P(i32 x) {
				ctor(str s) { }
				ctor(bool b) { }
			}
			""";

		var (_, diagnostics) = BindWithDiagnostics(source);
		var reported = diagnostics.Where(diagnostic => diagnostic.Code == "KRN2190").ToList();
		Assert.Equal(2, reported.Count);

		// Каждая диагностика указывает на объявление своего конструктора, а не на тип целиком.
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var type = Assert.Single(
			parsed.Tree.Declarations.OfType<TypeDeclaration>(),
			declaration => declaration.Name == "P");
		var offenders = type.DeclaredMembers.OfType<ConstructorMemberNode>().ToList();
		Assert.Equal(2, offenders.Count);
		foreach (var offender in offenders)
			Assert.Contains(reported, diagnostic => diagnostic.Span == offender.Span);
	}

	[Fact]
	public void Secondary_constructor_without_primary_constructor_may_omit_the_chain()
	{
		var diagnostics = Diagnostics("""
			type Q {
				i32 value
				ctor(i32 value) { $.value = value }
			}
			""");

		Assert.Empty(diagnostics);
	}

	[Fact]
	public void Chain_with_an_incompatible_argument_reports_overload_failure()
	{
		var diagnostics = Diagnostics("""
			type P(i32 x) {
				ctor(str s) : $(s) { }
			}
			""");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN1401", diagnostic.Code);
	}

	[Fact]
	public void Chain_to_itself_is_rejected()
	{
		var diagnostics = Diagnostics("""
			type Q {
				ctor(i32 x) : $(x) { }
			}
			""");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN1401", diagnostic.Code);
		Assert.Contains("сам", diagnostic.Message);
	}

	[Fact]
	public void Two_constructor_cycle_reports_one_KRN2191_on_the_last_declared_chain()
	{
		var module = BindWithDiagnostics("""
			type P(i32 x) {
				ctor(i64 a) : $(1f64) { }
				ctor(f64 a) : $(1i64) { }
			}
			""").Module;

		var diagnostic = Assert.Single(module.Diagnostics, d => d.Code == "KRN2191");
		Assert.Contains("P", diagnostic.Message);
		Assert.Contains("цикл", diagnostic.Message);

		// Правило представителя: конструктор, объявленный последним; span — его цепочка.
		var last = Assert.Single(module.Functions, function =>
			function.Symbol.Owner?.Name == "P" &&
			function.Symbol.Kind == FunctionKind.Constructor &&
			function.Symbol.Parameters[0].Type.Equals(PrimitiveType.F64));
		Assert.NotNull(last.Chain);
		Assert.Equal(last.Chain.Span, diagnostic.Span);

		// В цикле два конструктора, но диагностика одна.
		Assert.Single(module.Diagnostics, d => d.Code == "KRN2191");
	}

	[Fact]
	public void Three_constructor_cycle_reports_one_KRN2191()
	{
		var module = BindWithDiagnostics("""
			type Q {
				ctor(i32 a) : $("s") { }
				ctor(str a) : $(1i64) { }
				ctor(i64 a) : $(1i32) { }
			}
			""").Module;

		var diagnostic = Assert.Single(module.Diagnostics, d => d.Code == "KRN2191");
		Assert.Contains("Q", diagnostic.Message);

		var last = Assert.Single(module.Functions, function =>
			function.Symbol.Owner?.Name == "Q" &&
			function.Symbol.Kind == FunctionKind.Constructor &&
			function.Symbol.Parameters[0].Type.Equals(PrimitiveType.I64));
		Assert.Equal(last.Chain!.Span, diagnostic.Span);
	}

	[Fact]
	public void Cycle_in_a_type_without_primary_constructor_is_reported()
	{
		// Без первичного конструктора цепочка необязательна, но цикл из двух вторичных
		// всё равно никогда не завершит построение.
		var diagnostics = Diagnostics("""
			type Q {
				i32 value
				ctor(i32 a) : $(1i64) { }
				ctor(i64 a) : $(1i32) { }
			}
			""");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN2191", diagnostic.Code);
		Assert.NotEqual(default(TextSpan), diagnostic.Span);
	}

	[Fact]
	public void Diamond_merging_into_a_root_reports_nothing()
	{
		// Цепочка у конструктора одна (исходящее ребро только одно), поэтому «ромб» вырождается
		// в слияние путей к корню: str -> bool -> primary, f64 -> primary. Все пути заканчиваются корнем.
		var diagnostics = Diagnostics("""
			type P(i32 x) {
				ctor(f64 f) : $(1i32) { }
				ctor(bool b) : $(2i32) { }
				ctor(str s) : $(true) { }
			}
			""");

		Assert.Empty(diagnostics);
	}

	[Fact]
	public void Chain_of_constructors_reaching_a_root_reports_nothing()
	{
		var diagnostics = Diagnostics("""
			type P(i32 x) {
				ctor(str s) : $(1i32) { }
				ctor(bool b) : $(0f64) { }
				ctor(f64 f) : $(1i32) { }
			}
			""");

		Assert.Empty(diagnostics);
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
