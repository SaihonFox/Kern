using Kern.Bytecode;

namespace Kern.Compiler.Optimization;

/// <summary>
/// Инлайнинг <c>#[inline]</c>-функций (spec §7): код вызываемого переносится в место
/// вызова, параметры и локальные слоты получают новые слоты вызывающего, <c>Ret</c>
/// заменяется переходом в конец встроенного фрагмента. Поддерживаются ветвления, циклы
/// и локальные переменные; async/генераторы, конструкторы, memo и ref-опкоды не
/// встраиваются.
/// </summary>
public static class Inliner
{
	public static KbcModule Inline(KbcModule module)
	{
		ArgumentNullException.ThrowIfNull(module);
		for (var index = 0; index < module.FunctionPool.Count; index++)
		{
			FunctionDef function = module.FunctionPool[index];
			if ((function.Flags & MemberFlags.Native) != 0 || function.Code.Length == 0)
				continue;
			function.Code = InlineFunction(module, index, function);
		}

		return module;
	}

	private static int[] InlineFunction(KbcModule module, int callerIndex, FunctionDef caller)
	{
		Instruction[] instructions = Decode(caller.Code);
		var output = new List<int>(caller.Code.Length);
		var offsetMap = new Dictionary<int, int>(instructions.Length);
		var jumps = new List<(int At, int Target)>();
		var nextLocal = caller.LocalCount;
		foreach (Instruction instruction in instructions)
		{
			int outputStart = output.Count;
			offsetMap[instruction.Start] = outputStart;
			if (instruction.Op == OpCode.Call &&
				TryInline(module, callerIndex, caller, instruction, outputStart, ref nextLocal, out List<Instruction> replacement))
			{
				foreach (Instruction item in replacement)
				{
					output.Add((int)item.Op);
					output.AddRange(item.Operands);
				}
				continue;
			}

			if (OpCodeInfo.IsJump(instruction.Op))
				jumps.Add((outputStart, instruction.Operands[0]));
			output.Add((int)instruction.Op);
			output.AddRange(instruction.Operands);
		}

		foreach (var (at, target) in jumps)
			if (offsetMap.TryGetValue(target, out int mapped))
				output[at + 1] = mapped;

		if (nextLocal != caller.LocalCount)
			caller.LocalCount = nextLocal;
		caller.LineTable = RemapLines(caller.LineTable, offsetMap);
		return [.. output];
	}

	private static bool TryInline(
		KbcModule module,
		int callerIndex,
		FunctionDef caller,
		Instruction call,
		int outputStart,
		ref int nextLocal,
		out List<Instruction> replacement)
	{
		replacement = [];
		if (call.Operands.Length != 2)
			return false;
		int targetIndex = call.Operands[0];
		int argc = call.Operands[1];
		if ((uint)targetIndex >= (uint)module.FunctionPool.Count)
			return false;
		FunctionDef callee = module.FunctionPool[targetIndex];
		if (targetIndex == callerIndex ||
			!callee.Attributes.Any(attribute => attribute.Name == "inline") ||
			callee.Attributes.Any(attribute => attribute.Name == "no_inline") ||
			!CanInline(callee, argc))
			return false;

		int parameterBase = nextLocal;
		int localBase = parameterBase + argc;
		int localCount = System.Math.Max(0, callee.LocalCount - callee.ParamCount);
		nextLocal = localBase + localCount;

		// Аргументы лежат на стеке в порядке arg0..argN-1; снимаем с конца.
		for (int argument = argc - 1; argument >= 0; argument--)
			replacement.Add(new Instruction(0, OpCode.StoreLocal, [parameterBase + argument]));

		Instruction[] body = Decode(callee.Code);
		var newStart = new Dictionary<int, int>(body.Length);
		var position = outputStart;
		foreach (Instruction store in replacement)
			position += 1 + OpCodeInfo.OperandCount(store.Op);
		foreach (Instruction instruction in body)
		{
			newStart[instruction.Start] = position;
			position += 1 + OpCodeInfo.OperandCount(instruction.Op) + (instruction.Op == OpCode.Ret ? 1 : 0);
		}

		int end = position;
		foreach (Instruction instruction in body)
		{
			int[] operands = instruction.Operands;
			switch (instruction.Op)
			{
				case OpCode.Ret:
					replacement.Add(new Instruction(0, OpCode.Jmp, [end]));
					continue;
				case OpCode.LoadLocal or OpCode.StoreLocal or OpCode.SaveSp or OpCode.RestoreSp:
					operands = [MapSlot(operands[0], callee, parameterBase, localBase)];
					break;
				case OpCode.Jmp or OpCode.JmpIf or OpCode.JmpIfNot:
					operands = [newStart.GetValueOrDefault(operands[0], end)];
					break;
			}

			replacement.Add(new Instruction(0, instruction.Op, operands));
		}

		return true;
	}

	private static int MapSlot(int slot, FunctionDef callee, int parameterBase, int localBase) =>
		slot < callee.ParamCount
			? parameterBase + slot
			: localBase + (slot - callee.ParamCount);

	private static bool CanInline(FunctionDef callee, int argc)
	{
		if ((callee.Flags & (MemberFlags.Native | MemberFlags.Abstract | MemberFlags.Async |
			MemberFlags.Generator | MemberFlags.Memo | MemberFlags.Ctor)) != 0)
			return false;
		if (callee.ParamCount != argc || callee.Code.Length == 0)
			return false;

		var offset = 0;
		var hasReturn = false;
		while (offset < callee.Code.Length)
		{
			var op = (OpCode)callee.Code[offset];
			int count = OpCodeInfo.OperandCount(op);
			if (op == OpCode.Ret)
				hasReturn = true;
			if (op is OpCode.Stop or OpCode.Yield or OpCode.TailCall or OpCode.MatchFail
				or OpCode.RefLocal or OpCode.RefField or OpCode.RefStatic or OpCode.LoadRef
				or OpCode.StoreRef)
				return false;
			offset += 1 + count;
		}

		return hasReturn;
	}

	private static List<LineEntry> RemapLines(List<LineEntry> lines, Dictionary<int, int> offsetMap)
	{
		var result = new List<LineEntry>(lines.Count);
		foreach (LineEntry line in lines)
			if (offsetMap.TryGetValue(line.Offset, out int mapped))
				result.Add(new LineEntry(mapped, line.Line));
		return result;
	}

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
