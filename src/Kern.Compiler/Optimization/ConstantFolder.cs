using Kern.Bytecode;

namespace Kern.Compiler.Optimization;

/// <summary>
/// Свёртка констант (<c>-O1</c>) как самостоятельный проход по готовому <see cref="KbcModule"/>.
/// Складываются соседние литералы и операции над ними (арифметика, сравнения, битовые
/// операции, сдвиги, <c>!</c>, безопасные <c>Conv*</c>), результат записывается теми же
/// каноническими инструкциями, поэтому вид значения на стеке не меняется.
///
/// Инварианты: число «чистых» значений на стеке не меняется; инструкция, на которую ведёт
/// переход, никогда не становится второй половиной свёрнутой последовательности, а её
/// смещение пересчитывается вместе с остальными целями; таблица строк перестраивается по
/// сохранившимся инструкциям. Модуль меняется на месте и возвращается для удобства цепочек.
/// </summary>
public static class ConstantFolder
{
	public static KbcModule Fold(KbcModule module)
	{
		ArgumentNullException.ThrowIfNull(module);

		foreach (FunctionDef function in module.FunctionPool)
		{
			if (function.IsNative || function.Code.Length == 0)
				continue;

			var instructions = Decode(function);
			if (instructions is null)
				continue;
			if (FoldFunction(module, instructions))
				Rebuild(function, instructions);
		}

		return module;
	}

	/// <summary>Свёрнутая инструкция вместе с исходными смещениями (для целей переходов и строк).</summary>
	private sealed class Instruction
	{
		public required OpCode Op { get; init; }

		public required int[] Operands { get; init; }

		/// <summary>Смещения кода, которые после свёртки указывают на эту инструкцию.</summary>
		public List<int> OriginalOffsets { get; } = [];

		public int Size => 1 + Operands.Length;
	}

	private static List<Instruction>? Decode(FunctionDef function)
	{
		var instructions = new List<Instruction>();
		int[] code = function.Code;
		int pc = 0;
		while (pc < code.Length)
		{
			int start = pc;
			if (!OpCodeInfo.IsDefined(code[pc]))
				return null;
			OpCode op = (OpCode)code[pc++];
			int operandCount = OpCodeInfo.OperandCount(op);
			if (pc + operandCount > code.Length)
				return null;

			int[] operands = new int[operandCount];
			for (int index = 0; index < operandCount; index++)
				operands[index] = code[pc++];

			var instruction = new Instruction { Op = op, Operands = operands };
			instruction.OriginalOffsets.Add(start);
			instructions.Add(instruction);
		}

		return instructions;
	}

	private static bool FoldFunction(KbcModule module, List<Instruction> instructions)
	{
		var targets = new HashSet<int>();
		foreach (Instruction instruction in instructions)
			if (OpCodeInfo.IsJump(instruction.Op) && instruction.Operands.Length > 0)
				targets.Add(instruction.Operands[0]);

		bool folded = false;
		int index = 0;
		while (index < instructions.Count)
		{
			// Одно значение может состоять из литерала и цепочки его преобразований.
			if (!TryReadConstant(module, instructions, index, out var left, out int afterLeft))
			{
				index++;
				continue;
			}

			if (TryFoldBinary(module, instructions, targets, index, left, afterLeft))
			{
				folded = true;
				index = Math.Max(index - 1, 0);
				continue;
			}

			if (TryFoldUnary(module, instructions, targets, index, left, afterLeft))
			{
				folded = true;
				index = Math.Max(index - 1, 0);
				continue;
			}

			if (afterLeft > index + 1 &&
				CanFold(instructions, targets, index, afterLeft - 1) &&
				Fits(instructions, index, afterLeft - 1, left))
			{
				Replace(module, instructions, index, afterLeft - 1, left);
				folded = true;
				index = Math.Max(index - 1, 0);
				continue;
			}

			index++;
		}

		return folded;
	}

	private static bool TryFoldBinary(
		KbcModule module,
		List<Instruction> instructions,
		HashSet<int> targets,
		int start,
		ConstantValue left,
		int secondStart)
	{
		if (secondStart >= instructions.Count)
			return false;
		if (!TryReadConstant(module, instructions, secondStart, out var right, out int opIndex))
			return false;
		if (opIndex >= instructions.Count)
			return false;

		OpCode op = instructions[opIndex].Op;
		if (!TryBinary(op, left, right, out var result))
			return false;
		if (!CanFold(instructions, targets, start, opIndex) || !Fits(instructions, start, opIndex, result))
			return false;

		Replace(module, instructions, start, opIndex, result);
		return true;
	}

	private static bool TryFoldUnary(
		KbcModule module,
		List<Instruction> instructions,
		HashSet<int> targets,
		int start,
		ConstantValue operand,
		int opIndex)
	{
		if (opIndex >= instructions.Count)
			return false;

		if (!TryUnary(instructions[opIndex].Op, operand, out var result))
			return false;
		if (!CanFold(instructions, targets, start, opIndex) || !Fits(instructions, start, opIndex, result))
			return false;

		Replace(module, instructions, start, opIndex, result);
		return true;
	}

	/// <summary>Свёрнутая последовательность не должна содержать цель перехода, кроме первой инструкции.</summary>
	private static bool CanFold(
		IReadOnlyList<Instruction> instructions,
		HashSet<int> targets,
		int start,
		int end)
	{
		for (int index = start + 1; index <= end; index++)
			foreach (int offset in instructions[index].OriginalOffsets)
				if (targets.Contains(offset))
					return false;
		return true;
	}

	/// <summary>Замена имеет смысл, только если код стал короче.</summary>
	private static bool Fits(
		IReadOnlyList<Instruction> instructions,
		int start,
		int end,
		ConstantValue replacement)
	{
		var existing = 0;
		for (int index = start; index <= end; index++)
			existing += instructions[index].Size;
		return ConstantValue.EstimatedSize(replacement) < existing;
	}

	private static void Replace(
		KbcModule module,
		List<Instruction> instructions,
		int start,
		int end,
		ConstantValue value)
	{
		var replacement = Materialize(module, value);
		for (int index = start; index <= end; index++)
			foreach (int offset in instructions[index].OriginalOffsets)
				replacement[0].OriginalOffsets.Add(offset);

		instructions.RemoveRange(start, end - start + 1);
		instructions.InsertRange(start, replacement);
	}

	private static List<Instruction> Materialize(KbcModule module, ConstantValue value)
	{
		var slots = new List<int>();
		value.Emit(slots, module);

		var result = new List<Instruction>(2);
		int pc = 0;
		while (pc < slots.Count)
		{
			OpCode op = (OpCode)slots[pc++];
			int operandCount = OpCodeInfo.OperandCount(op);
			int[] operands = new int[operandCount];
			for (int index = 0; index < operandCount; index++)
				operands[index] = slots[pc++];
			result.Add(new Instruction { Op = op, Operands = operands });
		}

		return result;
	}

	/// <summary>Литерал и цепочка преобразований над ним.</summary>
	private static bool TryReadConstant(
		KbcModule module,
		IReadOnlyList<Instruction> instructions,
		int start,
		out ConstantValue value,
		out int next)
	{
		value = default;
		next = start;
		if (start >= instructions.Count || !TryReadPush(module, instructions[start], out value))
			return false;

		next = start + 1;
		while (next < instructions.Count &&
			ConstantValue.TryConversionTarget(instructions[next].Op, out var target))
		{
			if (!ConstantValue.TryConvert(value, target, out value))
				return false;
			next++;
		}

		return true;
	}

	private static bool TryReadPush(KbcModule module, Instruction instruction, out ConstantValue value)
	{
		value = default;
		switch (instruction.Op)
		{
			case OpCode.PushTrue:
				value = ConstantValue.Bool(true);
				return true;
			case OpCode.PushFalse:
				value = ConstantValue.Bool(false);
				return true;
			case OpCode.PushNil:
				value = ConstantValue.Nil;
				return true;
			case OpCode.PushNone:
				value = ConstantValue.None;
				return true;
			case OpCode.PushVoid:
				value = ConstantValue.Void;
				return true;
			case OpCode.PushI32:
				value = ConstantValue.Integer(PrimKind.I32, instruction.Operands[0]);
				return true;
			case OpCode.PushI64:
				value = ConstantValue.Integer(PrimKind.I64, CombineWords(instruction.Operands));
				return true;
			case OpCode.PushF64:
				value = new ConstantValue(PrimKind.F64, CombineWords(instruction.Operands), null);
				return true;
			case OpCode.PushConst:
				{
					int constIndex = instruction.Operands[0];
					if ((uint)constIndex >= (uint)module.ConstPool.Count)
						return false;
					return TryReadConst(module.ConstPool[constIndex], out value);
				}
			default:
				return false;
		}
	}

	private static bool TryReadConst(KConst constant, out ConstantValue value)
	{
		value = constant.Kind switch
		{
			KConstKind.Nil => ConstantValue.Nil,
			KConstKind.None => ConstantValue.None,
			KConstKind.Void => ConstantValue.Void,
			KConstKind.Bool => ConstantValue.Bool(constant.AsBool),
			KConstKind.I64 => ConstantValue.Integer(PrimKind.I64, constant.Bits),
			KConstKind.F64 => new ConstantValue(PrimKind.F64, constant.Bits, null),
			KConstKind.Char => ConstantValue.Integer(PrimKind.Char, (ushort)constant.AsChar),
			KConstKind.Str => ConstantValue.Str(constant.AsStr),
			_ => default,
		};
		return constant.Kind <= KConstKind.Str;
	}

	private static long CombineWords(int[] operands) =>
		unchecked((long)(((ulong)(uint)operands[1] << 32) | (uint)operands[0]));

	private static bool TryUnary(OpCode op, ConstantValue operand, out ConstantValue result)
	{
		switch (op)
		{
			case OpCode.Not:
				if (operand.Kind == PrimKind.Bool)
				{
					result = ConstantValue.Bool(!operand.AsBool);
					return true;
				}
				break;

			case OpCode.Neg:
				if (ConstantValue.IsSigned(operand.Kind))
				{
					result = ConstantValue.Integer(operand.Kind, unchecked(-operand.Bits));
					return true;
				}
				if (ConstantValue.IsFloat(operand.Kind))
				{
					result = ConstantValue.Float(operand.Kind, -operand.AsDouble);
					return true;
				}
				break;

			case OpCode.BitNot:
				if (ConstantValue.IsInteger(operand.Kind))
				{
					result = ConstantValue.Integer(operand.Kind, unchecked((long)~(ulong)operand.Bits));
					return true;
				}
				break;
		}

		result = default;
		return false;
	}

	private static bool TryBinary(OpCode op, ConstantValue left, ConstantValue right, out ConstantValue result)
	{
		result = default;
		switch (op)
		{
			case OpCode.Eq:
				result = ConstantValue.Bool(ConstantValue.ValuesEqual(left, right));
				return true;
			case OpCode.Ne:
				result = ConstantValue.Bool(!ConstantValue.ValuesEqual(left, right));
				return true;
			case OpCode.Lt or OpCode.Le or OpCode.Gt or OpCode.Ge:
				return TryRelational(op, left, right, out result);
			case OpCode.And:
				if (left.Kind == PrimKind.Bool && right.Kind == PrimKind.Bool)
				{
					result = ConstantValue.Bool(left.AsBool && right.AsBool);
					return true;
				}
				return false;
			case OpCode.Or:
				if (left.Kind == PrimKind.Bool && right.Kind == PrimKind.Bool)
				{
					result = ConstantValue.Bool(left.AsBool || right.AsBool);
					return true;
				}
				return false;
			case OpCode.BitAnd or OpCode.BitOr or OpCode.BitXor or OpCode.Shl or OpCode.Shr:
				return TryBitwise(op, left, right, out result);
			case OpCode.Add or OpCode.Sub or OpCode.Mul or OpCode.Div or OpCode.Mod or OpCode.Pow:
				return TryArithmetic(op, left, right, out result);
			default:
				return false;
		}
	}

	private static bool TryRelational(OpCode op, ConstantValue left, ConstantValue right, out ConstantValue result)
	{
		result = default;
		if (ConstantValue.IsNumber(left.Kind) && ConstantValue.IsNumber(right.Kind))
		{
			if (ConstantValue.IsFloat(left.Kind) || ConstantValue.IsFloat(right.Kind))
			{
				double x = left.AsDouble;
				double y = right.AsDouble;
				result = ConstantValue.Bool(op switch
				{
					OpCode.Lt => x < y,
					OpCode.Le => x <= y,
					OpCode.Gt => x > y,
					_ => x >= y,
				});
				return true;
			}

			result = ConstantValue.Bool(Apply(op, ConstantValue.CompareIntegers(left, right)));
			return true;
		}

		if (left.Kind == PrimKind.Char && right.Kind == PrimKind.Char)
		{
			result = ConstantValue.Bool(Apply(op, left.AsChar.CompareTo(right.AsChar)));
			return true;
		}

		if (left.Kind == PrimKind.Str && right.Kind == PrimKind.Str)
		{
			result = ConstantValue.Bool(Apply(op, string.CompareOrdinal(left.Text, right.Text)));
			return true;
		}

		return false;
	}

	private static bool Apply(OpCode op, int comparison) => op switch
	{
		OpCode.Lt => comparison < 0,
		OpCode.Le => comparison <= 0,
		OpCode.Gt => comparison > 0,
		_ => comparison >= 0,
	};

	private static bool TryBitwise(OpCode op, ConstantValue left, ConstantValue right, out ConstantValue result)
	{
		result = default;
		if (!ConstantValue.IsInteger(left.Kind) || !ConstantValue.IsInteger(right.Kind))
			return false;
		// VM требует совпадения видов у `& | ^`; у сдвигов вид берётся с левого операнда.
		if (op is not (OpCode.Shl or OpCode.Shr) && left.Kind != right.Kind)
			return false;

		int mask = ConstantValue.Width(left.Kind) == 64 ? 63 : 31;
		int count = (int)(right.AsU64 & (ulong)mask);
		ulong bits = op switch
		{
			OpCode.BitAnd => (ulong)left.Bits & (ulong)right.Bits,
			OpCode.BitOr => (ulong)left.Bits | (ulong)right.Bits,
			OpCode.BitXor => (ulong)left.Bits ^ (ulong)right.Bits,
			OpCode.Shl => (ulong)left.Bits << count,
			_ => ConstantValue.IsSigned(left.Kind)
				? (ulong)(left.Bits >> count)
				: (ulong)left.Bits >> count,
		};
		result = ConstantValue.Integer(left.Kind, unchecked((long)bits));
		return true;
	}

	private static bool TryArithmetic(OpCode op, ConstantValue left, ConstantValue right, out ConstantValue result)
	{
		result = default;
		if (op == OpCode.Add && left.Kind == PrimKind.Str && right.Kind == PrimKind.Str)
		{
			result = ConstantValue.Str((left.Text ?? string.Empty) + (right.Text ?? string.Empty));
			return true;
		}

		// VM требует одинаковый вид операндов: разные виды означали бы панику, а не значение.
		if (!ConstantValue.IsNumber(left.Kind) || left.Kind != right.Kind)
			return false;

		if (ConstantValue.IsFloat(left.Kind))
		{
			result = ConstantValue.Float(left.Kind, FloatResult(left.AsDouble, right.AsDouble, op));
			return true;
		}

		if (op is OpCode.Div or OpCode.Mod && right.AsU64 == 0)
			return false;

		if (ConstantValue.IsSigned(left.Kind))
		{
			if (op == OpCode.Pow && right.Bits < 0)
				return false;
			result = ConstantValue.Integer(left.Kind, SignedResult(left.Bits, right.Bits, op));
			return true;
		}

		result = ConstantValue.Integer(left.Kind, unchecked((long)UnsignedResult(left.AsU64, right.AsU64, op)));
		return true;
	}

	private static long SignedResult(long left, long right, OpCode op) => op switch
	{
		OpCode.Add => unchecked(left + right),
		OpCode.Sub => unchecked(left - right),
		OpCode.Mul => unchecked(left * right),
		OpCode.Div => left == long.MinValue && right == -1 ? long.MinValue : left / right,
		OpCode.Mod => left == long.MinValue && right == -1 ? 0 : left % right,
		_ => IntegerPow(left, right),
	};

	private static ulong UnsignedResult(ulong left, ulong right, OpCode op) => op switch
	{
		OpCode.Add => unchecked(left + right),
		OpCode.Sub => unchecked(left - right),
		OpCode.Mul => unchecked(left * right),
		OpCode.Div => left / right,
		OpCode.Mod => left % right,
		_ => UnsignedPow(left, right),
	};

	private static double FloatResult(double left, double right, OpCode op) => op switch
	{
		OpCode.Add => left + right,
		OpCode.Sub => left - right,
		OpCode.Mul => left * right,
		OpCode.Div => left / right,
		OpCode.Mod => left % right,
		_ => Math.Pow(left, right),
	};

	private static long IntegerPow(long value, long exponent)
	{
		long result = 1;
		long factor = value;
		ulong power = unchecked((ulong)exponent);
		while (power != 0)
		{
			if ((power & 1) != 0)
				result = unchecked(result * factor);
			power >>= 1;
			if (power != 0)
				factor = unchecked(factor * factor);
		}
		return result;
	}

	private static ulong UnsignedPow(ulong value, ulong exponent)
	{
		ulong result = 1;
		ulong factor = value;
		while (exponent != 0)
		{
			if ((exponent & 1) != 0)
				result = unchecked(result * factor);
			exponent >>= 1;
			if (exponent != 0)
				factor = unchecked(factor * factor);
		}
		return result;
	}

	private static void Rebuild(FunctionDef function, List<Instruction> instructions)
	{
		var offsets = new int[instructions.Count];
		int position = 0;
		for (int index = 0; index < instructions.Count; index++)
		{
			offsets[index] = position;
			position += instructions[index].Size;
		}

		var indexByOldOffset = new Dictionary<int, int>();
		var lines = new List<(int Old, int New)>();
		for (int index = 0; index < instructions.Count; index++)
			foreach (int offset in instructions[index].OriginalOffsets)
			{
				indexByOldOffset[offset] = index;
				lines.Add((offset, offsets[index]));
			}

		lines.Add((function.Code.Length, position));
		lines.Sort((left, right) => left.Old.CompareTo(right.Old));

		int[] code = new int[position];
		for (int index = 0; index < instructions.Count; index++)
		{
			Instruction instruction = instructions[index];
			int at = offsets[index];
			code[at] = (int)instruction.Op;
			for (int operand = 0; operand < instruction.Operands.Length; operand++)
			{
				int value = instruction.Operands[operand];
				if (operand == 0 && OpCodeInfo.IsJump(instruction.Op) &&
					indexByOldOffset.TryGetValue(value, out int target))
				{
					value = offsets[target];
				}
				code[at + 1 + operand] = value;
			}
		}

		function.Code = code;
		function.LineTable = RemapLines(function.LineTable, lines);
	}

	private static List<LineEntry> RemapLines(IReadOnlyList<LineEntry> table, List<(int Old, int New)> mapping)
	{
		var result = new List<LineEntry>(table.Count);
		foreach (LineEntry entry in table)
		{
			int offset = MapOffset(mapping, entry.Offset);
			if (result.Count > 0 && result[^1].Offset >= offset)
				continue;
			result.Add(new LineEntry(offset, entry.Line));
		}
		return result;
	}

	/// <summary>Последнее исходное смещение не больше данного; иначе начало кода.</summary>
	private static int MapOffset(List<(int Old, int New)> mapping, int offset)
	{
		int low = 0;
		int high = mapping.Count - 1;
		int found = -1;
		while (low <= high)
		{
			int middle = (low + high) / 2;
			if (mapping[middle].Old <= offset)
			{
				found = middle;
				low = middle + 1;
			}
			else
			{
				high = middle - 1;
			}
		}
		return found >= 0 ? mapping[found].New : 0;
	}
}
