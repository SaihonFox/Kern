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
/// M3-D: цепочки конструкторов <c>: $(...)</c> (spec §8, п.829). Вызванный конструктор получает
/// тот же <c>$</c>, инициализаторы выполняет только корень цепочки, пролог не повторяется.
/// </summary>
public sealed class ConstructorChainTests
{
	[Fact]
	public void Secondary_constructor_chains_to_the_primary()
	{
		const string source =
			"""
			type P(i32 x, i32 y) {
				fun sum() -> i32 { $x + $y }
				ctor(i32 x) : $(x, 0) { }
			}

			fun f() -> i32 {
				P p = P(5)
				p.sum()
			}
			""";

		Assert.Equal(KValue.I32(5), Run(source, "f"));
	}

	[Fact]
	public void Chain_can_go_through_another_secondary_constructor()
	{
		const string source =
			"""
			type P(i32 x, i32 y, i32 z) {
				fun sum() -> i32 { $x + $y + $z }
				ctor(i32 x, i32 y) : $(x, y, 0) { }
				ctor(i32 x) : $(x, 10) { }
			}

			fun f() -> i32 {
				P p = P(5)
				p.sum()
			}
			""";

		Assert.Equal(KValue.I32(15), Run(source, "f"));
	}

	[Fact]
	public void Chained_constructor_does_not_repeat_the_root_body_assignments()
	{
		const string source =
			"""
			type Counter {
				i32 value = 1
				ctor(i32 start) { $.value = 2 }
				ctor(str tag) : $(0) { }
			}

			fun f() -> i32 {
				Counter counter = Counter("x")
				counter.value
			}
			""";

		Assert.Equal(KValue.I32(2), Run(source, "f"));
	}

	[Fact]
	public void Chained_constructor_emits_no_initializer_stores_and_calls_the_target()
	{
		const string source =
			"""
			type Counter {
				i32 value = 1
				ctor(i32 start) { }
				ctor(str tag) : $(0) { }
			}

			fun f() -> i32 {
				Counter counter = Counter("x")
				counter.value
			}
			""";

		var result = Compile(source);
		var type = Assert.Single(result.Module.TypePool, definition => definition.Name == "Counter");

		// Конструкторы попадают в TypeDef в порядке объявления: ctor(i32) — корень, ctor(str) — цепочка.
		Assert.Equal(2, type.Ctors.Count);
		var chained = result.Module.FunctionPool[type.Ctors[1]];
		Assert.Equal("ctor", chained.Name);

		// Вторичный ctor(str) обязан вызвать ctor(i32): LoadLocal 0, PushI32, Call, Pop.
		var instructions = Decode(chained.Code);
		Assert.DoesNotContain(instructions, instruction => instruction.Op == OpCode.StoreField);
		var call = Assert.Single(instructions, instruction => instruction.Op == OpCode.Call);
		Assert.Equal(type.Ctors[0], call.Operands[0]);
		Assert.Equal(2, call.Operands[1]);
	}

	[Fact]
	public void Chained_constructor_assigns_its_own_dollar_parameter()
	{
		const string source =
			"""
			type Widget(i32 k) {
				str name = "default"
				ctor(str $name, i32 k) : $(k) { }
				fun label() -> str { $.name }
				fun key() -> i32 { $.k }
			}

			fun label_of() -> str {
				Widget widget = Widget("a", 7)
				widget.label()
			}

			fun key_of() -> i32 {
				Widget widget = Widget("a", 7)
				widget.key()
			}
			""";

		var result = Compile(source);
		Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.IsError);
		var interpreter = new Interpreter(result.Module);

		// Инициализатор поля ("default") цепочка не повторяет, но собственный `$name` обязан записать.
		Assert.Equal(KValue.Str("a"), interpreter.Run("label_of"));
		// Цепочка вызвала первичный конструктор: поле `k` вторичного ctor не трогает.
		Assert.Equal(KValue.I32(7), interpreter.Run("key_of"));
	}

	[Fact]
	public void Chained_constructor_copies_its_own_dollar_parameters_after_the_chain_call()
	{
		const string source =
			"""
			type Widget(i32 k) {
				str name = "default"
				ctor(str $name, i32 k) : $(k) { }
				fun label() -> str { $.name }
			}

			fun f() -> str { Widget("a", 7).label() }
			""";

		var result = Compile(source);
		var type = Assert.Single(result.Module.TypePool, definition => definition.Name == "Widget");

		// Цепочка — единственный конструктор типа с вызовом (Call) в коде.
		FunctionDef? chained = null;
		foreach (var index in type.Ctors)
		{
			var candidate = result.Module.FunctionPool[index];
			if (Decode(candidate.Code).Any(instruction => instruction.Op == OpCode.Call))
			{
				Assert.Null(chained);
				chained = candidate;
			}
		}

		Assert.NotNull(chained);
		var instructions = Decode(chained!.Code);
		var call = Assert.Single(instructions, instruction => instruction.Op == OpCode.Call);
		var store = Assert.Single(instructions, instruction => instruction.Op == OpCode.StoreField);
		Assert.True(
			store.Offset > call.Offset,
			"Автоприсваивание `$name` идёт после вызова цепочки, а не до него.");
	}

	/// <summary>Инструкция байткода: смещение в коде, опкод и его операнды.</summary>
	private readonly record struct Instruction(int Offset, OpCode Op, int[] Operands);

	/// <summary>
	/// Разбор кода на инструкции по раскладке <see cref="OpCodeInfo"/>: поиск опкода в сырых
	/// <c>int[]</c> мог бы совпасть с операндом, который равен номеру опкода.
	/// </summary>
	private static List<Instruction> Decode(int[] code)
	{
		var instructions = new List<Instruction>(code.Length);
		var offset = 0;
		while (offset < code.Length)
		{
			Assert.True(
				OpCodeInfo.IsDefined(code[offset]),
				$"Неизвестный опкод {code[offset]} по смещению {offset}.");
			var op = (OpCode)code[offset];
			var operandCount = OpCodeInfo.OperandCount(op);
			Assert.True(
				offset + operandCount < code.Length,
				$"Инструкция {op} по смещению {offset} обрезана.");
			var operands = new int[operandCount];
			Array.Copy(code, offset + 1, operands, 0, operandCount);
			instructions.Add(new Instruction(offset, op, operands));
			offset += 1 + operandCount;
		}

		return instructions;
	}

	private static KValue Run(string source, string function, params KValue[] arguments)
	{
		var result = Compile(source);
		Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.IsError);
		return new Interpreter(result.Module).Run(function, arguments);
	}

	private static CodegenResult Compile(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		var bound = new Binder(module, table).Bind();
		Assert.DoesNotContain(bound.Diagnostics, diagnostic => diagnostic.IsError);
		return new CodeGenerator().Generate(bound);
	}
}
