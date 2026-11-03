using Kern.Bytecode;

namespace Kern.Compiler.Optimization;

/// <summary>
/// Точный <c>MaxStack</c> функции: обход CFG с высотой стека и максимумом после каждой
/// инструкции. Запускается после кодогенерации и после оптимизаций (они меняют код).
/// </summary>
public static class StackAnalyzer
{
	public static KbcModule Compute(KbcModule module)
	{
		ArgumentNullException.ThrowIfNull(module);
		foreach (FunctionDef function in module.FunctionPool)
		{
			if ((function.Flags & MemberFlags.Native) != 0 || function.Code.Length == 0)
				continue;
			function.MaxStack = ComputeFunction(function, module);
		}

		return module;
	}

	public static int ComputeFunction(FunctionDef function, KbcModule module)
	{
		Instruction[] instructions = Decode(function.Code);
		if (instructions.Length == 0)
			return 0;

		Dictionary<int, int> byStart = [];
		for (var index = 0; index < instructions.Length; index++)
			byStart[instructions[index].Start] = index;

		var heights = new int[instructions.Length];
		Array.Fill(heights, -1);
		heights[0] = 0;
		var queue = new Queue<int>();
		queue.Enqueue(0);
		var max = 0;
		while (queue.Count > 0)
		{
			int index = queue.Dequeue();
			Instruction instruction = instructions[index];
			int height = heights[index];
			(int pop, int push) = Effect(instruction, module);
			if (height < pop)
				throw new KbcFormatException(
					$"функция '{function.Name}': стек пуст перед {instruction.Op} (нужно {pop}, есть {height}); код: [{string.Join(", ", function.Code)}]");

			int after = height - pop + push;
			if (after > max)
				max = after;

			if (Successors(instruction, index, instructions.Length, byStart, out int first, out int second))
				Propagate(first, after, heights, queue);
			if (second >= 0)
				Propagate(second, after, heights, queue);
		}

		return max;
	}

	private static void Propagate(int index, int height, int[] heights, Queue<int> queue)
	{
		if (index < 0 || index >= heights.Length)
			return;
		if (heights[index] >= height)
			return;
		heights[index] = height;
		queue.Enqueue(index);
	}

	private static bool Successors(
		Instruction instruction,
		int index,
		int count,
		Dictionary<int, int> byStart,
		out int first,
		out int second)
	{
		first = -1;
		second = -1;
		switch (instruction.Op)
		{
			case OpCode.Ret or OpCode.Stop or OpCode.TailCall or OpCode.MatchFail:
				return false;
			case OpCode.Jmp:
				first = byStart.GetValueOrDefault(instruction.Operands[0], -1);
				return first >= 0;
			case OpCode.JmpIf or OpCode.JmpIfNot:
				first = byStart.GetValueOrDefault(instruction.Operands[0], -1);
				second = index + 1 < count ? index + 1 : -1;
				return first >= 0 || second >= 0;
			default:
				first = index + 1 < count ? index + 1 : -1;
				// Yield приостанавливает кадр и продолжается со следующей инструкции.
				return first >= 0;
		}
	}

	private static (int Pop, int Push) Effect(Instruction instruction, KbcModule module) =>
		instruction.Op switch
		{
			// Без изменений стека.
			OpCode.Nop or OpCode.Swap or OpCode.Jmp or OpCode.SaveSp or OpCode.Stop
				or OpCode.RestoreSp or OpCode.CopyUnit or OpCode.TypeOfValue or OpCode.IsType
				or OpCode.Await or OpCode.ValueHash or OpCode.ValueHashFields or OpCode.CloneObject
				or OpCode.TupleGet or OpCode.NewCell or OpCode.LoadCell or OpCode.LoadRef
				or OpCode.RefField or OpCode.StrChars or OpCode.CharsToStr => (0, 0),

			// Один операнд на входе, один на выходе.
			OpCode.Neg or OpCode.Not or OpCode.BitNot or OpCode.ConvI8 or OpCode.ConvI16
				or OpCode.ConvI32 or OpCode.ConvI64 or OpCode.ConvU8 or OpCode.ConvU16 or OpCode.ConvU32
				or OpCode.ConvU64 or OpCode.ConvF16 or OpCode.ConvF32 or OpCode.ConvF64 or OpCode.ConvChar
				or OpCode.ConvBool or OpCode.LoadField or OpCode.ListLen => (1, 1),

			// Два входа, один выход.
			OpCode.Add or OpCode.Sub or OpCode.Mul or OpCode.Div or OpCode.Mod or OpCode.Pow
				or OpCode.Eq or OpCode.Ne or OpCode.Lt or OpCode.Le or OpCode.Gt or OpCode.Ge
				or OpCode.And or OpCode.Or or OpCode.BitAnd or OpCode.BitOr or OpCode.BitXor
				or OpCode.Shl or OpCode.Shr or OpCode.ValueCompare or OpCode.ValueCompareFields
				or OpCode.Repeat or OpCode.ListAppend or OpCode.ListAppendRange
				or OpCode.ListGet or OpCode.DictGet or OpCode.StrGet or OpCode.RefEq or OpCode.RefNe => (2, 1),

			// Три входа, один выход.
			OpCode.ListSlice or OpCode.Slice or OpCode.DictSet => (3, 1),

			// Вход/выход по операндам.
			OpCode.Call or OpCode.CallVirtual or OpCode.CallBase => (instruction.Operands[1], 1),
			OpCode.TailCall => (instruction.Operands[1], 0),
			OpCode.CallValue => (instruction.Operands[0] + 1, 1),
			OpCode.MakeClosure => (instruction.Operands[1], 1),
			OpCode.NewList or OpCode.NewArray => (instruction.Operands[1], 1),
			OpCode.NewTuple => (module.TypeSigListPool[instruction.Operands[0]].Length, 1),

			// Только вход.
			OpCode.Pop or OpCode.StoreLocal or OpCode.Ret or OpCode.JmpIf
				or OpCode.JmpIfNot or OpCode.Yield => (1, 0),
			OpCode.StoreField => (2, 1),
			OpCode.StoreStatic => (1, 1),
			OpCode.StoreRef or OpCode.StoreCell => (2, 0),
			OpCode.ListSet => (3, 0),

			// Только выход.
			OpCode.Dup or OpCode.PushConst or OpCode.PushI32 or OpCode.PushI64 or OpCode.PushF64
				or OpCode.PushTrue or OpCode.PushFalse or OpCode.PushNil or OpCode.PushNone
				or OpCode.PushVoid or OpCode.LoadLocal or OpCode.LoadStatic or OpCode.LoadCapture
				or OpCode.TypeOfSig or OpCode.MakeFunc or OpCode.NewObject or OpCode.NewObjectGeneric
				or OpCode.NewDict or OpCode.RefLocal or OpCode.RefStatic => (0, 1),

			// Остальные срезы.
			OpCode.SliceStep => (4, 1),
			OpCode.SliceFrom => (2, 1),
			OpCode.SliceStepFrom => (3, 1),
			_ => (0, 0),
		};


	private static Instruction[] Decode(int[] code)
	{
		var instructions = new List<Instruction>();
		var offset = 0;
		while (offset < code.Length)
		{
			var op = (OpCode)code[offset];
			int count = OpCodeInfo.OperandCount(op);
			var operands = new int[count];
			Array.Copy(code, offset + 1, operands, 0, count);
			instructions.Add(new Instruction(offset, op, operands));
			offset += 1 + count;
		}

		return [.. instructions];
	}

	private sealed record Instruction(int Start, OpCode Op, int[] Operands);
}
