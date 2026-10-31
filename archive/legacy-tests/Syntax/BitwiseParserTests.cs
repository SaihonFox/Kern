using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Syntax;

/// <summary>
/// B1: побитовые операторы `&amp; | ^ ~ &lt;&lt; &gt;&gt;` и составные присваивания.
/// Приоритеты и ассоциативность — ровно как в C# (spec §3): `|` ниже `^`, `^` ниже `&`,
/// `&` ниже сравнений, а сдвиги ниже сложения; унарный `~` привязывается к операнду.
/// </summary>
public sealed class BitwiseParserTests
{
	[Fact]
	public void Bitwise_and_binds_tighter_than_or()
	{
		// `a | b & c` — это `a | (b & c)`, а не `(a | b) & c`.
		var result = Parser.ParseExpression("a | b & c");

		Assert.Empty(result.Diagnostics);
		var or = Assert.IsType<BinaryExpression>(result.Expression);
		Assert.Equal("|", or.Operator);
		Assert.Equal("a", Assert.IsType<IdentifierExpression>(or.Left).Name);
		var and = Assert.IsType<BinaryExpression>(or.Right);
		Assert.Equal("&", and.Operator);
		Assert.Equal("b", Assert.IsType<IdentifierExpression>(and.Left).Name);
		Assert.Equal("c", Assert.IsType<IdentifierExpression>(and.Right).Name);
	}

	[Fact]
	public void Bitwise_xor_binds_tighter_than_or()
	{
		// `a ^ b | c` — это `(a ^ b) | c`: `^` приоритетнее `|`.
		var result = Parser.ParseExpression("a ^ b | c");

		Assert.Empty(result.Diagnostics);
		var or = Assert.IsType<BinaryExpression>(result.Expression);
		Assert.Equal("|", or.Operator);
		var xor = Assert.IsType<BinaryExpression>(or.Left);
		Assert.Equal("^", xor.Operator);
		Assert.Equal("c", Assert.IsType<IdentifierExpression>(or.Right).Name);
	}

	[Fact]
	public void Equality_binds_tighter_than_bitwise_and()
	{
		// `a & b == c` — это `a & (b == c)`: сравнение приоритетнее `&`.
		var result = Parser.ParseExpression("a & b == c");

		Assert.Empty(result.Diagnostics);
		var and = Assert.IsType<BinaryExpression>(result.Expression);
		Assert.Equal("&", and.Operator);
		var equality = Assert.IsType<BinaryExpression>(and.Right);
		Assert.Equal("==", equality.Operator);
	}

	[Fact]
	public void Additive_binds_tighter_than_left_shift()
	{
		// `a << 1 + 2` — это `a << (1 + 2)`: сдвиг ниже сложения.
		var result = Parser.ParseExpression("a << 1 + 2");

		Assert.Empty(result.Diagnostics);
		var shift = Assert.IsType<BinaryExpression>(result.Expression);
		Assert.Equal("<<", shift.Operator);
		var sum = Assert.IsType<BinaryExpression>(shift.Right);
		Assert.Equal("+", sum.Operator);
	}

	[Fact]
	public void Additive_binds_tighter_than_right_shift()
	{
		// `a + b >> c` — это `(a + b) >> c`.
		var result = Parser.ParseExpression("a + b >> c");

		Assert.Empty(result.Diagnostics);
		var shift = Assert.IsType<BinaryExpression>(result.Expression);
		Assert.Equal(">>", shift.Operator);
		var sum = Assert.IsType<BinaryExpression>(shift.Left);
		Assert.Equal("+", sum.Operator);
		Assert.Equal("c", Assert.IsType<IdentifierExpression>(shift.Right).Name);
	}

	[Fact]
	public void Bitwise_not_applies_to_its_operand_before_addition()
	{
		// `~x + 1` — это `(~x) + 1`, а не `~(x + 1)`.
		var result = Parser.ParseExpression("~x + 1");

		Assert.Empty(result.Diagnostics);
		var add = Assert.IsType<BinaryExpression>(result.Expression);
		Assert.Equal("+", add.Operator);
		var not = Assert.IsType<UnaryExpression>(add.Left);
		Assert.Equal("~", not.Operator);
		Assert.Equal("x", Assert.IsType<IdentifierExpression>(not.Operand).Name);
		Assert.IsType<IntegerLiteralExpression>(add.Right);
	}

	[Fact]
	public void Shifts_are_left_associative()
	{
		// `a << b << c` — это `(a << b) << c`.
		var result = Parser.ParseExpression("a << b << c");

		Assert.Empty(result.Diagnostics);
		var outer = Assert.IsType<BinaryExpression>(result.Expression);
		Assert.Equal("<<", outer.Operator);
		var inner = Assert.IsType<BinaryExpression>(outer.Left);
		Assert.Equal("<<", inner.Operator);
		Assert.Equal("c", Assert.IsType<IdentifierExpression>(outer.Right).Name);
	}

	[Fact]
	public void Compound_bitwise_assignments_parse_as_assignment_statements()
	{
		// Все пять составных операторов — отдельные операторы присваивания с текстом из двух/трёх символов.
		var result = Parser.Parse("""
			fun f {
				i32 x = 0
				x &= 1
				x |= 2
				x ^= 3
				x <<= 4
				x >>= 5
			}
			""");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		var body = Assert.IsType<BlockStatement>(function.Body);
		var assignments = body.Statements.Skip(1).Select(statement => Assert.IsType<AssignmentStatement>(statement)).ToList();

		Assert.Equal(["&=", "|=", "^=", "<<=", ">>="], assignments.Select(assignment => assignment.Operator));
		Assert.All(assignments, assignment =>
			Assert.Equal("x", Assert.IsType<IdentifierExpression>(assignment.Target).Name));
	}
}
