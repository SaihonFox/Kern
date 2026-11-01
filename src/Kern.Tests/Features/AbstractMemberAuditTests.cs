using Kern.Binding.Catalog;
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
/// Реализация абстрактных методов (spec §2): конкретный <c>type</c> обязан реализовать все
/// оставшиеся унаследованные <c>@fun</c>. Реализацией считается собственный метод, явный
/// выбор родителя <c>$[A]foo</c> или конкретная реализация в предке; конкретный член
/// несвязанного родителя сам по себе не считается реализацией. Нарушение — <c>KRN2101</c>.
/// </summary>
public sealed class AbstractMemberAuditTests
{
	[Fact]
	public void Concrete_type_without_abstract_implementation_reports_KRN2101()
	{
		var diagnostics = CompileWithDiagnostics("""
			@type Shape {
				@fun area() -> f64
			}

			type Circle(f64 radius)[Shape] { }
			""").Diagnostics;

		var error = Assert.Single(diagnostics, diagnostic => diagnostic.IsError);
		Assert.Equal("KRN2101", error.Code);
		Assert.Contains("Circle", error.Message, StringComparison.Ordinal);
		Assert.Contains("area", error.Message, StringComparison.Ordinal);
		Assert.Contains("Shape", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Override_satisfies_abstract_member()
	{
		Compile("""
			@type Shape {
				@fun area() -> f64
			}

			type Circle(f64 radius)[Shape] {
				~fun area() -> f64 3.0 * radius
			}
			""");
	}

	[Fact]
	public void Abstract_type_may_leave_inherited_member_abstract()
	{
		Compile("""
			@type Shape {
				@fun area() -> f64
			}

			@type Rounded[Shape] { }
			""");
	}

	[Fact]
	public void Parent_selector_can_satisfy_another_parents_abstract_member()
	{
		Compile("""
			@type A {
				@fun area() -> f64
			}

			open type B {
				fun area() -> f64 2.0
			}

			type C()[A, B] {
				fun $[B]area() -> f64
			}
			""");
	}

	[Fact]
	public void Concrete_method_of_unrelated_parent_does_not_satisfy_abstract_member()
	{
		var diagnostics = CompileWithDiagnostics("""
			@type A {
				@fun area() -> f64
			}

			open type B {
				fun area() -> f64 2.0
			}

			type C()[A, B] { }
			""").Diagnostics;

		var error = Assert.Single(diagnostics, diagnostic => diagnostic.IsError);
		Assert.Equal("KRN2101", error.Code);
		Assert.Contains("C", error.Message, StringComparison.Ordinal);
		Assert.Contains("area", error.Message, StringComparison.Ordinal);
		Assert.Contains("из 'A'", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Concrete_ancestor_implementation_is_inherited_by_its_descendant()
	{
		Compile("""
			@type A {
				@fun area() -> f64
			}

			type B(f64 value)[A] {
				~fun area() -> f64 value
			}

			type C()[B(1.0)] { }
			""");
	}

	[Fact]
	public void Generic_ancestor_parameters_do_not_cause_false_positive()
	{
		Compile("""
			@type Box<T> {
				@fun map(list<T> xs) -> list<T>
			}

			type IntBox()[Box<i32>] {
				~fun map(list<i32> xs) -> list<i32> xs
			}
			""");
	}

	[Fact]
	public void Example_06_generics_operators_still_checks_clean()
	{
		var path = FindExample("06_generics_operators");
		Assert.NotNull(path);
		Compile(File.ReadAllText(path!));
	}

	/// <summary>Ищет исходник примера от каталога сборки вверх до корня репозитория.</summary>
	private static string? FindExample(string name)
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null)
		{
			var candidate = Path.Combine(directory.FullName, "examples", name, "src", "main.kern");
			if (File.Exists(candidate))
				return candidate;
			directory = directory.Parent;
		}

		return null;
	}

	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

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
