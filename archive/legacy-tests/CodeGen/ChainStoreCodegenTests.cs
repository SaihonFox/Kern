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
/// M3-D4, Q13: байткод записи через цепь членов. Конечное звено оставляет приёмник
/// на стеке (по нему объемлющий уровень пишет изменённую копию), каждое unit-звено
/// возвращает объемлющее значение, а корень-локальная получает копию через
/// <c>StoreLocal</c> без лишнего <c>Pop</c>. Проверяются точные подпоследовательности
/// и отсутствие переполнений стека при исполнении.
/// </summary>
public sealed class ChainStoreCodegenTests
{
	[Fact]
	public void Nested_unit_store_emits_the_final_link_and_both_write_backs()
	{
		const string source =
			"""
			unit Inner(i32 x)
			unit Outer(Inner inner)

			fun f() -> i32 {
				Outer o = Outer(Inner(1))
				o.inner.x = 5
				o.inner.x
			}
			""";

		var result = Compile(source);
		var function = Function(result.Module, "f");
		var instructions = Decode(function);
		var code = instructions.Select(instruction => instruction.Op).ToList();

		// Звено `inner` (unit) копируется, конечное звено `x` оставляет приёмник
		// на стеке; затем обе обратные записи и запись корня-локальной.
		OpCode[] expected =
		[
			OpCode.LoadLocal,
			OpCode.CopyUnit,
			OpCode.Dup,
			OpCode.Dup,
			OpCode.LoadField,
			OpCode.CopyUnit,
			OpCode.Dup,
			OpCode.PushI32,
			OpCode.ConvI32,
			OpCode.StoreField,
			OpCode.Pop,
			OpCode.StoreField,
			OpCode.Pop,
			OpCode.StoreLocal,
		];

		var start = FindSequence(code, expected);
		Assert.True(start >= 0, $"Не найдена последовательность записи через цепь: {Describe(expected)}; код: {Describe(code)}");

		var outer = Type(result.Module, "Outer");
		var inner = Type(result.Module, "Inner");
		var innerSlot = Assert.Single(outer.Fields, field => field.Name == "inner").Slot;
		var xSlot = Assert.Single(inner.Fields, field => field.Name == "x").Slot;

		var slice = instructions.Skip(start).Take(expected.Length).ToList();
		var loads = slice.Where(instruction => instruction.Op == OpCode.LoadField).ToList();
		var stores = slice.Where(instruction => instruction.Op == OpCode.StoreField).ToList();
		Assert.Equal(innerSlot, Assert.Single(loads).Operands[0]);
		Assert.Equal([innerSlot, xSlot], stores.Select(instruction => instruction.Operands[0]));

		// Корень-локальная: обратная запись идёт в тот же слот, что и чтение.
		var rootLoad = slice[0];
		var rootStore = slice[^1];
		Assert.Equal(rootLoad.Operands[0], rootStore.Operands[0]);

		Verified(result.Module);
		Assert.Equal(KValue.I32(5), new Interpreter(result.Module).Run("f", []));
	}

	[Fact]
	public void Compound_assignment_reuses_the_receiver_and_stays_balanced()
	{
		const string source =
			"""
			unit Inner(i32 x)
			unit Outer(Inner inner)

			fun f() -> i32 {
				Outer o = Outer(Inner(1))
				o.inner.x += 2
				o.inner.x
			}
			""";

		var result = Compile(source);
		var code = Decode(Function(result.Module, "f")).Select(instruction => instruction.Op).ToList();

		// Текущее значение конечного звена читается уже внутри цепи: Dup приёмника
		// (конечное звено), Dup для чтения `x`, сложение и две обратные записи.
		OpCode[] expected =
		[
			OpCode.LoadLocal,
			OpCode.CopyUnit,
			OpCode.Dup,
			OpCode.Dup,
			OpCode.LoadField,
			OpCode.CopyUnit,
			OpCode.Dup,
			OpCode.Dup,
			OpCode.LoadField,
			OpCode.PushI32,
			OpCode.ConvI32,
			OpCode.Add,
			OpCode.StoreField,
			OpCode.Pop,
			OpCode.StoreField,
			OpCode.Pop,
			OpCode.StoreLocal,
		];

		var start = FindSequence(code, expected);
		Assert.True(start >= 0, $"Составное присваивание через цепь сгенерировано иначе: {Describe(expected)}; код: {Describe(code)}");

		Verified(result.Module);
		Assert.Equal(KValue.I32(3), new Interpreter(result.Module).Run("f", []));
	}

	[Fact]
	public void Three_level_chain_writes_every_unit_link_back()
	{
		const string source =
			"""
			unit Inner(i32 v)
			unit Mid(Inner inner)
			unit Outer(Mid mid)

			fun f() -> i32 {
				Outer o = Outer(Mid(Inner(1)))
				o.mid.inner.v = 9
				o.mid.inner.v
			}
			""";

		var result = Compile(source);
		var function = Function(result.Module, "f");
		var instructions = Decode(function);
		var code = instructions.Select(instruction => instruction.Op).ToList();

		// Два промежуточных unit-звена: у каждого свой LoadField/CopyUnit и запись
		// обратно, поэтому в конце три StoreField (v, inner, mid) и StoreLocal корня.
		OpCode[] expected =
		[
			OpCode.LoadLocal,
			OpCode.CopyUnit,
			OpCode.Dup,
			OpCode.Dup,
			OpCode.LoadField,
			OpCode.CopyUnit,
			OpCode.Dup,
			OpCode.Dup,
			OpCode.LoadField,
			OpCode.CopyUnit,
			OpCode.Dup,
			OpCode.PushI32,
			OpCode.ConvI32,
			OpCode.StoreField,
			OpCode.Pop,
			OpCode.StoreField,
			OpCode.Pop,
			OpCode.StoreField,
			OpCode.Pop,
			OpCode.StoreLocal,
		];

		var start = FindSequence(code, expected);
		Assert.True(start >= 0, $"Трёхуровневая цепь сгенерирована иначе: {Describe(expected)}; код: {Describe(code)}");

		var outer = Type(result.Module, "Outer");
		var mid = Type(result.Module, "Mid");
		var inner = Type(result.Module, "Inner");
		var midSlot = Assert.Single(outer.Fields, field => field.Name == "mid").Slot;
		var innerSlot = Assert.Single(mid.Fields, field => field.Name == "inner").Slot;
		var vSlot = Assert.Single(inner.Fields, field => field.Name == "v").Slot;
		var stores = instructions.Skip(start).Take(expected.Length)
			.Where(instruction => instruction.Op == OpCode.StoreField)
			.Select(instruction => instruction.Operands[0]);
		Assert.Equal([vSlot, innerSlot, midSlot], stores);

		Verified(result.Module);
		Assert.Equal(KValue.I32(9), new Interpreter(result.Module).Run("f", []));
	}

	[Fact]
	public void Set_only_final_property_is_written_through_its_setter()
	{
		// Конечному звену читать нечего: set-only свойство допустимо (Bug 4).
		// Setter пишет v.x в stored, поэтому чтение возвращает 5.
		const string source =
			"""
			unit Inner(i32 x)
			unit Box(i32 stored) {
				Inner inner {
					set(v) {
						$.stored = v.x
					}
				}
			}
			unit Holder(Box box)

			fun f() -> i32 {
				Holder h = Holder(Box(0))
				h.box.inner = Inner(5)
				h.box.stored
			}
			""";

		var result = Compile(source);
		Verified(result.Module);
		Assert.Equal(KValue.I32(5), new Interpreter(result.Module).Run("f", []));
	}

	/// <summary>Код модуля переживает запись/чтение .kbc и проходит проверку формата.</summary>
	private static void Verified(KbcModule module)
	{
		var decoded = KbcReader.Read(KbcWriter.Write(module));
		KbcModuleValidator.Validate(decoded);
		Assert.Equal(Function(module, "f").Code, Function(decoded, "f").Code);
	}

	private static FunctionDef Function(KbcModule module, string name) =>
		Assert.Single(module.FunctionPool, function => function.Name == name);

	private static TypeDef Type(KbcModule module, string name) =>
		Assert.Single(module.TypePool, type => type.Name == name);

	private static IReadOnlyList<Instruction> Decode(FunctionDef function)
	{
		var instructions = new List<Instruction>();
		var code = function.Code;
		var offset = 0;
		while (offset < code.Length)
		{
			var op = (OpCode)code[offset];
			var operandCount = OpCodeInfo.OperandCount(op);
			var operands = new int[operandCount];
			Array.Copy(code, offset + 1, operands, 0, operandCount);
			instructions.Add(new Instruction(op, operands));
			offset += 1 + operandCount;
		}

		return instructions;
	}

	private static int FindSequence(IReadOnlyList<OpCode> code, IReadOnlyList<OpCode> expected)
	{
		for (var start = 0; start + expected.Count <= code.Count; start++)
		{
			var matches = true;
			for (var offset = 0; offset < expected.Count; offset++)
			{
				if (code[start + offset] == expected[offset])
					continue;
				matches = false;
				break;
			}

			if (matches)
				return start;
		}

		return -1;
	}

	private static string Describe(IEnumerable<OpCode> code) => string.Join(" · ", code);

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
		var result = new CodeGenerator().Generate(bound);
		Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.IsError);
		return result;
	}

	private readonly record struct Instruction(OpCode Op, int[] Operands);
}
