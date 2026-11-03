using Kern.Bytecode;

namespace Kern.Compiler.Optimization;

/// <summary>
/// Инлайнинг <c>#[inline]</c>-функций (M11/spec §7): простая функция без ветвлений и
/// собственных локальных слотов встраивается в место вызова. Аргументы сохраняются в
/// новые локальные слоты вызывающего, параметры переназначаются на них; <c>Ret</c>
/// отбрасывается. Сложные функции (циклы, ветвления, генераторы, async, ref) не
/// встраиваются — они остаются обычными вызовами.
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
			offsetMap[instruction.Start] = output.Count;
			if (instruction.Op == OpCode.Call &&
				TryInline(module, callerIndex, caller, instruction, ref nextLocal, out List<Instruction> replacement))
			{
				foreach (Instruction item in replacement)
				{
					if (OpCodeInfo.IsJump(item.Op))
						jumps.Add((output.Count, item.Operands[0]));
					output.Add((int)item.Op);
					output.AddRange(item.Operands);
				}
				continue;
			}

			if (OpCodeInfo.IsJump(instruction.Op))
				jumps.Add((output.Count, instruction.Operands[0]));
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

		int baseSlot = nextLocal;
		nextLocal += argc;
		// Аргументы лежат на стеке в порядке arg0..argN-1; снимаем с конца.
		for (int argument = argc - 1; argument >= 0; argument--)
			replacement.Add(new Instruction(0, OpCode.StoreLocal, [baseSlot + argument]));

		Instruction[] body = Decode(callee.Code);
		for (var index = 0; index < body.Length; index++)
		{
			Instruction instruction = body[index];
			if (instruction.Op == OpCode.Ret)
				break;
			int[] operands = instruction.Operands;
			if (instruction.Op is OpCode.LoadLocal or OpCode.StoreLocal)
				operands = [baseSlot + instruction.Operands[0]];
			replacement.Add(new Instruction(0, instruction.Op, operands));
		}

		return true;
	}

	private static bool CanInline(FunctionDef callee, int argc)
	{
		if ((callee.Flags & (MemberFlags.Native | MemberFlags.Abstract | MemberFlags.Async |
			MemberFlags.Generator | MemberFlags.Memo | MemberFlags.Ctor)) != 0)
			return false;
		if (callee.ParamCount != argc || callee.LocalCount != callee.ParamCount)
			return false;
		if (callee.Code.Length < 2 || (OpCode)callee.Code[^1] != OpCode.Ret)
			return false;

		var returned = false;
		var offset = 0;
		while (offset < callee.Code.Length)
		{
			var op = (OpCode)callee.Code[offset];
			int count = OpCodeInfo.OperandCount(op);
			if (op == OpCode.Ret)
			{
				if (offset != callee.Code.Length - 1)
					return false;
				returned = true;
				break;
			}
			if (op is OpCode.Jmp or OpCode.JmpIf or OpCode.JmpIfNot or OpCode.Stop or OpCode.Yield
				or OpCode.TailCall or OpCode.MatchFail or OpCode.SaveSp or OpCode.RestoreSp
				or OpCode.RefLocal or OpCode.RefField or OpCode.RefStatic or OpCode.LoadRef
				or OpCode.StoreRef)
				return false;
			if (op is OpCode.LoadLocal or OpCode.StoreLocal && callee.Code[offset + 1] >= callee.ParamCount)
				return false;
			offset += 1 + count;
		}

		return returned;
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
