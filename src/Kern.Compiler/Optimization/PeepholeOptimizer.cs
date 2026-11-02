using Kern.Bytecode;

namespace Kern.Compiler.Optimization;

/// <summary>
/// Peephole-оптимизатор <c>-O2</c> (spec §16, раунд 6; M11): работает по готовому байткоду,
/// сохраняя семантику. Проходы: <c>Not; JmpIf</c> → <c>JmpIfNot</c>, прошивка цепочек
/// <c>Jmp</c>, удаление <c>Jmp</c> на следующую инструкцию, удаление пар «чистая загрузка +
/// <c>Pop</c>» (в т.ч. <c>PushVoid; Pop</c>) и недостижимого кода. Цели переходов и таблицы
/// строк пересчитываются; инструкции, бывшие целями, не исчезают молча — переход на удалённую
/// пару ведёт на следующую оставшуюся инструкцию, что эквивалентно.
/// </summary>
public static class PeepholeOptimizer
{
	/// <summary>Оптимизировать все функции модуля; возвращает тот же модуль (in-place).</summary>
	public static KbcModule Optimize(KbcModule module)
	{
		ArgumentNullException.ThrowIfNull(module);
		foreach (FunctionDef function in module.FunctionPool)
		{
			if ((function.Flags & MemberFlags.Native) != 0 || function.Code.Length == 0)
				continue;
			OptimizeFunction(function);
		}

		return module;
	}

	private static void OptimizeFunction(FunctionDef function)
	{
		List<Instruction> instructions = Decode(function.Code);
		Dictionary<int, int> byStart = [];
		for (var index = 0; index < instructions.Count; index++)
			byStart[instructions[index].Start] = index;

		ThreadJumps(instructions, byStart);
		FuseNotJump(instructions);
		RemoveJumpToNext(instructions);
		RemoveLoadPop(instructions, byStart);
		RemoveUnreachable(instructions);

		Compact(function, instructions);
	}

	private static List<Instruction> Decode(int[] code)
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

		return instructions;
	}

	private static void ThreadJumps(List<Instruction> instructions, Dictionary<int, int> byStart)
	{
		foreach (Instruction instruction in instructions)
		{
			if (!OpCodeInfo.IsJump(instruction.Op))
				continue;

			int target = instruction.Operands[0];
			HashSet<int> seen = [];
			while (byStart.TryGetValue(target, out int index) &&
				instructions[index].Op == OpCode.Jmp &&
				byStart.TryGetValue(instructions[index].Operands[0], out int next) &&
				seen.Add(target))
			{
				target = instructions[next].Start;
			}

			instruction.Operands[0] = target;
		}
	}

	private static void FuseNotJump(List<Instruction> instructions)
	{
		for (var index = 0; index + 1 < instructions.Count; index++)
		{
			Instruction current = instructions[index];
			Instruction next = instructions[index + 1];
			if (current.Op != OpCode.Not || next.Op != OpCode.JmpIf)
				continue;
			current.Op = OpCode.Nop;
			current.Operands = [];
			next.Op = OpCode.JmpIfNot;
		}
	}

	private static void RemoveJumpToNext(List<Instruction> instructions)
	{
		for (var index = 0; index + 1 < instructions.Count; index++)
			if (instructions[index].Op == OpCode.Jmp &&
				instructions[index].Operands[0] == instructions[index + 1].Start)
				instructions[index].Removed = true;
	}

	private static void RemoveLoadPop(List<Instruction> instructions, Dictionary<int, int> byStart)
	{
		for (var index = 0; index + 1 < instructions.Count; index++)
		{
			Instruction load = instructions[index];
			Instruction pop = instructions[index + 1];
			if (pop.Op != OpCode.Pop || !IsPurePush(load.Op))
				continue;

			// `Pop` не должен быть целью перехода: иначе переход «починил бы» стек,
			// которого больше нет.
			if (IsTarget(instructions, byStart, pop.Start))
				continue;

			load.Removed = true;
			pop.Removed = true;
			index++;
		}
	}

	private static void RemoveUnreachable(List<Instruction> instructions)
	{
		if (instructions.Count == 0)
			return;

		Dictionary<int, int> byStart = [];
		for (var index = 0; index < instructions.Count; index++)
			byStart[instructions[index].Start] = index;

		var reachable = new bool[instructions.Count];
		var stack = new Stack<int>();
		stack.Push(0);
		while (stack.Count > 0)
		{
			int index = stack.Pop();
			if (reachable[index])
				continue;
			reachable[index] = true;

			Instruction instruction = instructions[index];
			if (instruction.Op is OpCode.Ret or OpCode.Stop or OpCode.TailCall or OpCode.Yield)
				continue;

			if (OpCodeInfo.IsJump(instruction.Op) &&
				byStart.TryGetValue(instruction.Operands[0], out int target))
				stack.Push(target);
			if (instruction.Op != OpCode.Jmp && index + 1 < instructions.Count)
				stack.Push(index + 1);
		}

		for (var index = 0; index < instructions.Count; index++)
			if (!reachable[index])
				instructions[index].Removed = true;
	}

	private static void Compact(FunctionDef function, List<Instruction> instructions)
	{
		// `Nop` — чистый маркер (в т.ч. бывший `Not` или `SaveSp`): удаляем, цели ведут дальше.
		foreach (Instruction instruction in instructions)
			if (instruction.Op == OpCode.Nop)
				instruction.Removed = true;

		var code = new List<int>(function.Code.Length);
		foreach (Instruction instruction in instructions)
		{
			if (instruction.Removed)
				continue;
			instruction.NewStart = code.Count;
			code.Add((int)instruction.Op);
			code.AddRange(instruction.Operands);
		}

		// Старое смещение → новое: удалённая инструкция ведёт на первую оставшуюся после неё.
		var offsetMap = new Dictionary<int, int>(instructions.Count);
		var next = code.Count;
		for (var index = instructions.Count - 1; index >= 0; index--)
		{
			if (!instructions[index].Removed)
				next = instructions[index].NewStart;
			offsetMap[instructions[index].Start] = next;
		}

		foreach (Instruction instruction in instructions)
		{
			if (instruction.Removed || !OpCodeInfo.IsJump(instruction.Op))
				continue;

			if (!offsetMap.TryGetValue(instruction.Operands[0], out int mapped))
				continue;
			instruction.Operands[0] = mapped;
			code[instruction.NewStart + 1] = mapped;
		}

		function.LineTable = RemapLines(function.LineTable, offsetMap);
		function.Code = [.. code];
	}

	private static List<LineEntry> RemapLines(List<LineEntry> lines, Dictionary<int, int> offsetMap)
	{
		var result = new List<LineEntry>(lines.Count);
		foreach (LineEntry entry in lines)
		{
			if (!offsetMap.TryGetValue(entry.Offset, out int mapped))
				continue;
			if (result.Count > 0 && result[^1].Offset == mapped)
				result[^1] = new LineEntry(mapped, entry.Line);
			else
				result.Add(new LineEntry(mapped, entry.Line));
		}

		return result;
	}

	private static bool IsTarget(List<Instruction> instructions, Dictionary<int, int> byStart, int start) =>
		instructions.Any(instruction =>
			!instruction.Removed && OpCodeInfo.IsJump(instruction.Op) && instruction.Operands[0] == start);

	private static bool IsPurePush(OpCode op) => op is
		OpCode.PushConst or OpCode.PushI32 or OpCode.PushI64 or OpCode.PushF64 or
		OpCode.PushTrue or OpCode.PushFalse or OpCode.PushNil or OpCode.PushNone or
		OpCode.PushVoid or OpCode.LoadLocal or OpCode.LoadStatic or OpCode.LoadCapture;

	private sealed class Instruction(int start, OpCode op, int[] operands)
	{
		public int Start { get; } = start;

		public OpCode Op { get; set; } = op;

		public int[] Operands { get; set; } = operands;

		public bool Removed { get; set; }

		public int NewStart { get; set; }
	}
}
