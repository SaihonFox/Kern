using Kern.Bytecode;

namespace Kern.Tests.Bytecode;

/// <summary>Сборка кода вручную: опкоды + операнды, метки переходов через <see cref="Position"/>.</summary>
internal sealed class CodeBuilder
{
	private readonly List<int> _code = [];

	public int Position => _code.Count;

	public CodeBuilder Op(OpCode op)
	{
		_code.Add((int)op);
		return this;
	}

	public CodeBuilder Op(OpCode op, int operand)
	{
		_code.Add((int)op);
		_code.Add(operand);
		return this;
	}

	public CodeBuilder Op(OpCode op, int first, int second)
	{
		_code.Add((int)op);
		_code.Add(first);
		_code.Add(second);
		return this;
	}

	public CodeBuilder Push(int value) => Op(OpCode.PushI32, value);

	public CodeBuilder PushBool(bool value) => Op(value ? OpCode.PushTrue : OpCode.PushFalse);

	public CodeBuilder PushI64(long value)
	{
		_code.Add((int)OpCode.PushI64);
		_code.Add(unchecked((int)(value & 0xFFFFFFFF)));
		_code.Add(unchecked((int)(value >> 32)));
		return this;
	}

	public CodeBuilder PushF64(double value)
	{
		long bits = BitConverter.DoubleToInt64Bits(value);
		_code.Add((int)OpCode.PushF64);
		_code.Add(unchecked((int)(bits & 0xFFFFFFFF)));
		_code.Add(unchecked((int)(bits >> 32)));
		return this;
	}

	public CodeBuilder Load(int slot) => Op(OpCode.LoadLocal, slot);

	public CodeBuilder Store(int slot) => Op(OpCode.StoreLocal, slot);

	/// <summary>Переход с меткой: цель подставляется позже через <see cref="Patch"/>.</summary>
	public CodeBuilder Jump(OpCode op, int target)
	{
		_code.Add((int)op);
		_code.Add(target);
		return this;
	}

	/// <summary>Записать цель перехода в позицию операнда.</summary>
	public CodeBuilder Patch(int operandPosition, int target)
	{
		_code[operandPosition] = target;
		return this;
	}

	public int[] Build() => [.. _code];
}
