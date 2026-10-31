using Kern.Bytecode;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.CodeGen;

/// <summary>
/// Q2 из DECISIONS_NEEDED: регрессия на короткое замыкание <c>&amp;&amp;</c>/<c>||</c>
/// (spec §3, строка 108: правый операнд <c>&amp;&amp;</c> при <c>false</c> слева и <c>||</c> при
/// <c>true</c> слева не вычисляется).
/// Прежние тесты (<c>Logical_and_short_circuits</c>, <c>Logical_or_short_circuits</c>) проверяли
/// только значение выражения, поэтому прошли бы и при вычислении правого операнда.
/// Здесь правый операнд — вызов <c>bang()</c>, который падает <see cref="KernPanic"/>:
/// «правый вычислен» наблюдаемо как исключение, а не только как совпавшее значение.
/// </summary>
public sealed class ShortCircuitTests
{
	/// <summary>
	/// Наблюдаемый побочный эффект. Встроенный <c>panic(…)</c> пока не имеет байткода
	/// (<c>Builtin_call_is_reported_as_KRN2201</c>; нативная привязка — M6), поэтому паникуем
	/// делением на ноль: это обычный <see cref="KernPanic"/> VM с сообщением «деление на ноль».
	/// </summary>
	private const string BangFunction = """
		fun bang() -> bool {
			i32 zero = 0
			5 / zero > 0
		}
		""";

	[Fact]
	public void Bang_panics_when_called_directly()
	{
		var panic = RunExpectingPanic(WithBang("fun f() -> bool { bang() }"), "f");

		Assert.Equal("деление на ноль", panic.Message);
	}

	[Fact]
	public void False_and_does_not_evaluate_rhs()
	{
		var source = WithBang("fun f() -> bool { false && bang() }");

		Assert.Equal(KValue.False, Run(source, "f"));
	}

	[Fact]
	public void True_or_does_not_evaluate_rhs()
	{
		var source = WithBang("fun f() -> bool { true || bang() }");

		Assert.Equal(KValue.True, Run(source, "f"));
	}

	[Fact]
	public void True_and_evaluates_rhs()
	{
		var panic = RunExpectingPanic(WithBang("fun f() -> bool { true && bang() }"), "f");

		Assert.Equal("деление на ноль", panic.Message);
	}

	[Fact]
	public void False_or_evaluates_rhs()
	{
		var panic = RunExpectingPanic(WithBang("fun f() -> bool { false || bang() }"), "f");

		Assert.Equal("деление на ноль", panic.Message);
	}

	/// <summary>
	/// Тот же байткод, решение принимает значение операнда во время выполнения:
	/// <c>flag == false</c> пропускает правый операнд, <c>flag == true</c> — вычисляет его.
	/// </summary>
	[Fact]
	public void Runtime_left_operand_decides_whether_rhs_runs()
	{
		var source = WithBang(
			"""
			fun both(bool flag) -> bool { flag && bang() }
			fun either(bool flag) -> bool { flag || bang() }
			""");

		Assert.Equal(KValue.False, Run(source, "both", KValue.False));
		Assert.Equal("деление на ноль", RunExpectingPanic(source, "both", KValue.True).Message);
		Assert.Equal(KValue.True, Run(source, "either", KValue.True));
		Assert.Equal("деление на ноль", RunExpectingPanic(source, "either", KValue.False).Message);
	}

	[Fact]
	public void Mixed_expression_skips_rhs_inside_and()
	{
		var source = WithBang("fun f(i32 x, i32 y) -> bool { (x > 0 && bang()) || y > 5 }");

		Assert.Equal(KValue.True, Run(source, "f", KValue.I32(0), KValue.I32(10)));
		Assert.Equal(KValue.False, Run(source, "f", KValue.I32(0), KValue.I32(0)));
		Assert.Equal("деление на ноль", RunExpectingPanic(source, "f", KValue.I32(1), KValue.I32(10)).Message);
	}

	/// <summary>
	/// Результат операторов для всех четырёх наборов операндов: проверяет и значение,
	/// и что ветка пропуска правого операнда оставляет правильный литерал.
	/// </summary>
	[Fact]
	public void Logical_operators_return_truth_table_values()
	{
		var source =
			"""
			fun both(bool a, bool b) -> bool { a && b }
			fun either(bool a, bool b) -> bool { a || b }
			""";

		(KValue A, KValue B, KValue And, KValue Or)[] cases =
		[
			(KValue.True, KValue.True, KValue.True, KValue.True),
			(KValue.True, KValue.False, KValue.False, KValue.True),
			(KValue.False, KValue.True, KValue.False, KValue.True),
			(KValue.False, KValue.False, KValue.False, KValue.False)
		];

		foreach (var (a, b, expectedAnd, expectedOr) in cases)
		{
			Assert.Equal(expectedAnd, Run(source, "both", a, b));
			Assert.Equal(expectedOr, Run(source, "either", a, b));
		}
	}

	private static string WithBang(string tail) => BangFunction + "\n" + tail;

	private static KValue Run(string source, string function, params KValue[] arguments)
	{
		var module = Compile(source);
		return new Interpreter(module).Run(function, arguments);
	}

	private static KernPanic RunExpectingPanic(string source, string function, params KValue[] arguments)
	{
		var module = Compile(source);
		return Assert.Throws<KernPanic>(() => new Interpreter(module).Run(function, arguments));
	}

	private static KbcModule Compile(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		var bound = new Binder(module, table).Bind();
		Assert.DoesNotContain(bound.Diagnostics, diagnostic => diagnostic.IsError);
		var result = new CodeGenerator().Generate(bound);
		Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.IsError);
		return result.Module;
	}
}
