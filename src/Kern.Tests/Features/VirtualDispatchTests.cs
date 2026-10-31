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

namespace Kern.Tests.Features;

/// <summary>
/// Динамический диспатч методов класса (spec §8): <c>@fun</c>/<c>~fun</c> вызываются по
/// типу получателя, поэтому унаследованное тело видит переопределение наследника. Методы
/// <c>unit</c>/<c>unit!</c> остаются статическими, как и нативные вызовы, конструкторы и
/// расширения.
/// </summary>
public sealed class VirtualDispatchTests
{
	[Fact]
	public void Abstract_method_dispatches_through_list_of_parent()
	{
		const string source = """
			@type Shape {
				@fun area() -> f64
				fun describe() -> str f"площадь ${area():.2f}"
			}

			type Circle(f64 radius)[Shape] {
				~fun area() -> f64 3.0 * radius
			}

			type Square(f64 side)[Shape] {
				~fun area() -> f64 4.0 * side
			}

			fun circle_area() -> f64 {
				list<Shape> shapes = [Circle(1.0), Square(2.0)]
				shapes[0].area()
			}

			fun square_area() -> f64 {
				list<Shape> shapes = [Circle(1.0), Square(2.0)]
				shapes[1].area()
			}

			fun describe_first() -> str {
				list<Shape> shapes = [Circle(1.0), Square(2.0)]
				shapes[0].describe()
			}
			""";
		Assert.Equal(KValue.F64(3.0), Run(source, "circle_area"));
		Assert.Equal(KValue.F64(8.0), Run(source, "square_area"));
		Assert.Equal(KValue.Str("площадь 3.00"), Run(source, "describe_first"));
	}

	[Fact]
	public void Override_dispatches_through_parent_typed_parameter_and_local()
	{
		const string source = """
			open type Base {
				open fun name() -> str "base"
			}

			type Child()[Base] {
				~fun name() -> str "child"
			}

			fun through_parameter(Base value) -> str value.name()

			fun through_local() -> str {
				Base value = Child()
				value.name()
			}

			fun through_argument() -> str {
				Child item()
				through_parameter(item)
			}

			fun parameter_case() -> str through_parameter(Child())
			""";
		Assert.Equal(KValue.Str("child"), Run(source, "parameter_case"));
		Assert.Equal(KValue.Str("child"), Run(source, "through_local"));
		Assert.Equal(KValue.Str("child"), Run(source, "through_argument"));
	}

	[Fact]
	public void Inherited_method_without_override_still_binds()
	{
		const string source = """
			open type Base {
				fun increment(i32 value) -> i32 value + 1
				fun call_increment(i32 value) -> i32 increment(value)
			}

			type Child()[Base] { }

			fun own_instance() -> i32 {
				Child item()
				item.increment(4)
			}

			fun parent_view() -> i32 {
				Base item = Child()
				item.increment(4)
			}

			fun inherited_call() -> i32 {
				Base item = Child()
				item.call_increment(4)
			}
			""";
		Assert.Equal(KValue.I32(5), Run(source, "own_instance"));
		Assert.Equal(KValue.I32(5), Run(source, "parent_view"));
		Assert.Equal(KValue.I32(5), Run(source, "inherited_call"));
	}

	[Fact]
	public void Unit_method_call_stays_static()
	{
		const string unitSource = """
			unit Point(i32 x) {
				fun doubled() -> i32 x * 2
				fun plus(i32 n) -> i32 doubled() + n
			}

			fun unit_call() -> i32 {
				Point p(3)
				p.plus(1)
			}
			""";
		Assert.Equal(KValue.I32(7), Run(unitSource, "unit_call"));
		Assert.DoesNotContain(OpCode.CallVirtual, OpCodes(Compile(unitSource)));

		const string classSource = """
			open type Base {
				open fun name() -> str "base"
			}

			type Child()[Base] {
				~fun name() -> str "child"
			}

			fun class_call() -> str {
				Base item = Child()
				item.name()
			}
			""";
		Assert.Contains(OpCode.CallVirtual, OpCodes(Compile(classSource)));
	}

	private sealed record Outcome(CodegenResult? Code, IReadOnlyList<Diagnostic> Diagnostics);

	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	private static KValue Run(string source, string function, params KValue[] arguments) =>
		new Interpreter(Compile(source).Module, new NativeResolver(Catalog())).Run(function, arguments);

	private static CodegenResult Compile(string source)
	{
		var outcome = CompileWithDiagnostics(source);
		Assert.DoesNotContain(outcome.Diagnostics, diagnostic => diagnostic.IsError);
		Assert.NotNull(outcome.Code);
		return outcome.Code!;
	}

	/// <summary>Полный конвейер без интерпретатора: диагностики и байткод.</summary>
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

	/// <summary>Все опкоды всех функций модуля (для проверки выбора Call/CallVirtual).</summary>
	private static List<OpCode> OpCodes(CodegenResult code)
	{
		var result = new List<OpCode>();
		foreach (var function in code.Module.FunctionPool)
		{
			var instructions = function.Code;
			for (var offset = 0; offset < instructions.Length;)
			{
				var op = (OpCode)instructions[offset];
				result.Add(op);
				offset += 1 + OpCodeInfo.OperandCount(op);
			}
		}

		return result;
	}
}
