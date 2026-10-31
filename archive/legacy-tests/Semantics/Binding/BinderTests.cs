using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Xunit;

namespace Kern.Tests.Semantics.Binding;

public sealed class BinderTests
{
	[Fact]
	public void Empty_main_binds_to_void()
	{
		var module = Bind("fun main { }");

		var main = Assert.IsType<BoundFunction>(module.FindFunction("main"));
		Assert.Equal(PrimitiveType.Void, main.ReturnType);
		Assert.Empty(main.Body.Statements);
		Assert.True(main.Body.FallsThrough);
	}

	[Fact]
	public void Parameters_and_locals_get_slots()
	{
		var module = Bind("""
			fun f(i32 a) -> i32 {
				i32 b = a + 1
				b
			}
			""");

		var function = Assert.IsType<BoundFunction>(module.FindFunction("f"));
		Assert.Equal(0, Assert.Single(function.Parameters).Slot);

		var local = Assert.IsType<BoundLocalDeclaration>(function.Body.Statements[0]);
		Assert.Equal(1, local.Slot);
		Assert.Equal(PrimitiveType.I32, local.Type);

		var value = Assert.IsType<BoundVariableExpression>(function.Body.Value);
		Assert.Equal(1, value.Slot);
	}

	[Fact]
	public void Initializer_is_converted_to_declared_type()
	{
		var module = Bind("fun f { i64 x = 1 }");
		var function = module.FindFunction("f")!;
		var local = Assert.IsType<BoundLocalDeclaration>(function.Body.Statements[0]);

		var conversion = Assert.IsType<BoundConversionExpression>(local.Initializer);
		Assert.Equal(ConversionKind.NumericWidening, conversion.Conversion.Kind);
		Assert.Equal(PrimitiveType.I64, conversion.Type);
	}

	[Fact]
	public void Mixed_numerics_converge_to_f64()
	{
		var module = Bind("fun f { f64 x = 1 + 2.5 }");
		var function = module.FindFunction("f")!;
		var local = Assert.IsType<BoundLocalDeclaration>(function.Body.Statements[0]);
		Assert.Equal(PrimitiveType.F64, local.Type);
	}

	[Fact]
	public void Var_infers_the_literal_type()
	{
		var module = Bind("fun f { var x = 1 }");
		var function = module.FindFunction("f")!;
		var local = Assert.IsType<BoundLocalDeclaration>(function.Body.Statements[0]);
		Assert.Equal(PrimitiveType.I32, local.Type);
		Assert.False(local.IsSingleAssignment);
	}

	[Fact]
	public void If_expression_receives_the_common_branch_type()
	{
		var module = Bind("fun f(i32 c) -> i32 { if c > 0 { 1 } else { 2 } }");
		var function = module.FindFunction("f")!;

		var conditional = Assert.IsType<BoundIfExpression>(function.Body.Value);
		Assert.Equal(PrimitiveType.I32, conditional.Type);
		Assert.NotNull(conditional.Else);
	}

	[Fact]
	public void If_without_else_is_void()
	{
		var module = Bind("fun main { if true { println(1) } }");
		var function = module.FindFunction("main")!;
		var statement = Assert.IsType<BoundExpressionStatement>(function.Body.Statements[0]);
		var conditional = Assert.IsType<BoundIfExpression>(statement.Expression);

		Assert.Equal(PrimitiveType.Void, conditional.Type);
		Assert.Null(conditional.Else);
	}

	[Fact]
	public void Return_expression_is_converted_to_the_return_type()
	{
		var module = Bind("fun f() -> i64 { ret 1 }");
		var function = module.FindFunction("f")!;
		var ret = Assert.IsType<BoundReturn>(function.Body.Statements[0]);

		var conversion = Assert.IsType<BoundConversionExpression>(ret.Expression);
		Assert.Equal(ConversionKind.NumericWidening, conversion.Conversion.Kind);
	}

	[Fact]
	public void Wrong_return_type_reports_KRN2102()
	{
		var diagnostic = Assert.Single(Diagnostics("fun f() -> i32 { ret \"x\" }"));
		Assert.Equal("KRN2102", diagnostic.Code);
	}

	[Fact]
	public void Missing_return_reports_KRN2104()
	{
		var diagnostics = Diagnostics("fun f() -> i32 { }");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2104");
	}

	[Fact]
	public void Unknown_identifier_reports_KRN2103()
	{
		var diagnostics = Diagnostics("fun f { println(unknown) }");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2103");
	}

	[Fact]
	public void Unknown_function_reports_KRN2103()
	{
		var diagnostics = Diagnostics("fun f { missing(1) }");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2103");
	}

	[Fact]
	public void Single_assignment_reports_KRN2105_on_second_write()
	{
		var diagnostics = Diagnostics("""
			fun f {
				i32! x = 1
				x = 2
			}
			""");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2105");
	}

	[Fact]
	public void Reading_before_assignment_reports_KRN2105()
	{
		var diagnostics = Diagnostics("""
			fun f {
				i32 x
				println(x)
			}
			""");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2105");
	}

	[Fact]
	public void Duplicate_local_reports_KRN2106()
	{
		var diagnostics = Diagnostics("""
			fun f {
				i32 x = 1
				i32 x = 2
			}
			""");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2106");
	}

	[Fact]
	public void User_function_call_resolves_and_types()
	{
		var module = Bind("""
			fun inc(i32 x) -> i32 { x + 1 }
			fun f() -> i32 { inc(2) }
			""");
		var function = module.FindFunction("f")!;
		var call = Assert.IsType<BoundCallExpression>(function.Body.Value);

		Assert.Equal("inc", call.Function.Name);
		Assert.Equal(PrimitiveType.I32, call.Type);
		Assert.Single(call.Arguments);
	}

	[Fact]
	public void Println_builtin_binds_without_declarations()
	{
		var module = Bind("fun f { println(\"hi\") }");
		var function = module.FindFunction("f")!;
		var statement = Assert.IsType<BoundExpressionStatement>(function.Body.Statements[0]);
		var call = Assert.IsType<BoundCallExpression>(statement.Expression);

		Assert.Equal("println", call.Function.Name);
		Assert.Equal(PrimitiveType.Void, call.Type);
	}

	[Fact]
	public void Comparison_produces_bool()
	{
		var module = Bind("fun f(i32 a) -> bool { a < 3 }");
		var function = module.FindFunction("f")!;
		var comparison = Assert.IsType<BoundBinaryExpression>(function.Body.Value);
		Assert.Equal(PrimitiveType.Bool, comparison.Type);
	}

	[Fact]
	public void Logical_operator_requires_bool_operands()
	{
		var diagnostics = Diagnostics("fun f(i32 a) -> bool { a && true }");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2101");
	}

	[Fact]
	public void Main_with_string_list_parameter_binds_without_diagnostics()
	{
		// spec §13: main может принимать один аргумент list<str> с аргументами командной строки.
		var module = Bind("""
			fun main(list<str> args) {
				str first = args[0]
			}
			""");

		var main = Assert.IsType<BoundFunction>(module.FindFunction("main"));
		Assert.Equal("list<str>", Assert.Single(main.Parameters).Symbol.Type.ToString());
	}

	[Theory]
	[InlineData("fun main(i32 a) { }")]
	[InlineData("fun main(str arg) { }")]
	[InlineData("fun main(list<i32> args) { }")]
	[InlineData("fun main(list<str> a, list<str> b) { }")]
	public void Main_with_unsupported_parameters_reports_KRN2107(string source)
	{
		var diagnostics = Diagnostics(source);

		Assert.Contains(
			diagnostics,
			diagnostic => diagnostic.Code == "KRN2107" &&
				diagnostic.Message.Contains("list<str>", StringComparison.Ordinal));
	}

	[Fact]
	public void Compound_assignment_lowers_to_binary()
	{
		var module = Bind("""
			fun f {
				i32 x = 1
				x += 2
			}
			""");
		var assignment = Assert.IsType<BoundAssignment>(module.FindFunction("f")!.Body.Statements[1]);
		Assert.IsType<BoundBinaryExpression>(assignment.Value);
	}

	[Fact]
	public void String_concatenation_stays_str()
	{
		var module = Bind("fun f { str s = \"a\" + \"b\" }");
		var local = Assert.IsType<BoundLocalDeclaration>(module.FindFunction("f")!.Body.Statements[0]);
		Assert.Equal(PrimitiveType.Str, local.Type);
	}

	[Fact]
	public void Lifted_conversion_binds_i32_into_i64_nullable()
	{
		// Q8: подъём i32 -> i64? разрешён, потому что i32 -> i64 неявное.
		var module = Bind("fun f(i32 x) { i64? y = x }");
		var local = Assert.IsType<BoundLocalDeclaration>(module.FindFunction("f")!.Body.Statements[0]);

		Assert.Equal(NullableType.Create(PrimitiveType.I64), local.Type);
		var conversion = Assert.IsType<BoundConversionExpression>(local.Initializer);
		Assert.Equal(ConversionKind.Lifted, conversion.Conversion.Kind);
	}

	[Fact]
	public void Child_upcasts_into_a_nullable_base()
	{
		// Q8: Child -> Base? поднимает неявное Child -> Base.
		var module = Bind("""
			type Base { }
			type Child[Base] { }

			fun f -> Base? {
				Child c = Child()
				Base? b = c
				b
			}
			""");
		var local = Assert.IsType<BoundLocalDeclaration>(module.FindFunction("f")!.Body.Statements[1]);

		var nullable = Assert.IsType<NullableType>(local.Type);
		Assert.Equal("Base", Assert.IsType<ClassType>(nullable.Underlying).Symbol.Name);
		var conversion = Assert.IsType<BoundConversionExpression>(local.Initializer);
		Assert.Equal(ConversionKind.Lifted, conversion.Conversion.Kind);
	}

	[Theory]
	[InlineData("fun f(str a, str b) -> str { a & b }")]
	[InlineData("fun f(bool a, bool b) -> bool { a & b }")]
	[InlineData("fun f(bool a) -> bool { ~a }")]
	[InlineData("fun f(f64 a) -> f64 { ~a }")]
	public void Bitwise_operators_require_integers(string source) =>
		Assert.Contains(Diagnostics(source), diagnostic => diagnostic.Code == "KRN2101");

	[Fact]
	public void Bitwise_and_widens_operands_to_common_kind()
	{
		var module = Bind("fun f(i32 a, i64 b) -> i64 { a & b }");
		var and = Assert.IsType<BoundBinaryExpression>(module.FindFunction("f")!.Body.Value);

		Assert.Equal(PrimitiveType.I64, and.Type);
		Assert.Equal(PrimitiveType.I64, and.Left.Type);
		Assert.Equal(PrimitiveType.I64, and.Right.Type);
	}

	[Fact]
	public void Shift_count_keeps_left_operand_type_and_is_not_widened()
	{
		var unsigned = Assert.IsType<BoundBinaryExpression>(
			Bind("fun f(u8 a, i32 b) -> u8 { a << b }").FindFunction("f")!.Body.Value);
		Assert.Equal(PrimitiveType.U8, unsigned.Type);
		Assert.Equal(PrimitiveType.U8, unsigned.Left.Type);
		// Binder намеренно не расширяет счётчик сдвига: ширину маскирует VM (spec §3).
		Assert.Equal(PrimitiveType.I32, unsigned.Right.Type);

		var signed = Assert.IsType<BoundBinaryExpression>(
			Bind("fun g(i32 a, i64 b) -> i32 { a << b }").FindFunction("g")!.Body.Value);
		Assert.Equal(PrimitiveType.I32, signed.Type);
		Assert.Equal(PrimitiveType.I64, signed.Right.Type);
	}

	[Fact]
	public void Compound_shift_assignment_keeps_variable_type()
	{
		var module = Bind("""
			fun f {
				i32 x = 1
				x <<= 1
				x
			}
			""");
		var assignment = Assert.IsType<BoundAssignment>(module.FindFunction("f")!.Body.Statements[1]);

		Assert.Equal(PrimitiveType.I32, assignment.Type);
	}

	[Fact]
	public void Compound_shift_assignment_on_str_reports_error()
	{
		var diagnostics = Diagnostics("""
			fun f {
				str s = "a"
				s <<= 1
			}
			""");

		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2101");
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
