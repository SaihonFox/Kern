using Kern.Bytecode;

namespace Kern.Compiler.CodeGen;

/// <summary>Метка перехода: позиция известна после <see cref="CodeBuilder.Mark"/>, ссылки правятся при завершении.</summary>
public sealed class Label
{
	public int Position { get; internal set; } = -1;
	internal List<int> Fixups { get; } = [];
}

/// <summary>Накопитель кода одной функции: опкоды, метки, таблица строк.</summary>
public sealed class CodeBuilder
{
	private readonly List<int> _code = [];
	private int _instructions;

	public List<(int Offset, int Line)> LineTable { get; } = [];
	public int Position => _code.Count;
	public int InstructionCount => _instructions;

	public void Emit(OpCode op, params int[] operands)
	{
		_code.Add((int)op);
		_code.AddRange(operands);
		_instructions++;
	}

	public void Jump(OpCode op, Label target)
	{
		_code.Add((int)op);
		_instructions++;
		AddRef(target);
	}

	/// <summary>Switch n,t0..tn-1: цели — метки.</summary>
	public void EmitSwitch(IReadOnlyList<Label> targets)
	{
		_code.Add((int)OpCode.Switch);
		_instructions++;
		_code.Add(targets.Count);
		foreach (var t in targets) AddRef(t);
	}

	/// <summary>Переход по MatchType sig,target.</summary>
	public void EmitWithLabel(OpCode op, int operand, Label target)
	{
		_code.Add((int)op);
		_instructions++;
		_code.Add(operand);
		AddRef(target);
	}

	private void AddRef(Label l)
	{
		if (l.Position >= 0) _code.Add(l.Position);
		else
		{
			l.Fixups.Add(_code.Count);
			_code.Add(-1);
		}
	}

	public void Mark(Label l)
	{
		l.Position = _code.Count;
		foreach (int f in l.Fixups) _code[f] = l.Position;
		l.Fixups.Clear();
	}

	public void SetLine(int line)
	{
		if (line <= 0) return;
		if (LineTable.Count > 0 && LineTable[^1].Line == line) return;
		if (LineTable.Count > 0 && LineTable[^1].Offset == _code.Count) LineTable[^1] = (_code.Count, line);
		else LineTable.Add((_code.Count, line));
	}

	/// <summary>Перезаписывает уже выданные слова кода (замена инструкции той же длины).</summary>
	public void Overwrite(int position, params int[] words)
	{
		for (int i = 0; i < words.Length; i++) _code[position + i] = words[i];
	}

	public int[] ToArray() => [.. _code];
}
