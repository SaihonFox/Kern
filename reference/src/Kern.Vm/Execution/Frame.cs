using Kern.Bytecode;
using Kern.Vm.Async;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>
/// Фрейм вызова. Один массив Regs: [аргументы][локальные][стек операндов].
/// Фрейм — обычный объект кучи, поэтому async-функция может «запарковаться» (хранить фрейм в KTask) без потери стека.
/// </summary>
public sealed class Frame
{
	public required FunctionDef Def { get; init; }
	public int FuncIndex { get; init; }
	public required int[] Code { get; init; }
	public required KValue[] Regs { get; init; }
	/// <summary>Число слотов аргументов; локальная переменная slot лежит в Regs[ArgSlots + slot].</summary>
	public int ArgSlots { get; init; }
	public int Pc { get; set; }
	/// <summary>Абсолютный индекс вершины стека операндов в Regs (следующая свободная ячейка).</summary>
	public int Sp { get; set; }
	public KCell[] Captures { get; init; } = [];
	public KValue This { get; set; }
	public bool HasThis { get; init; }
	public KType[] ClassArgs { get; init; } = [];
	public KType[] MethodArgs { get; init; } = [];
	public Frame? Caller { get; set; }
	public int Depth { get; init; }
	/// <summary>Текущая строка исходника (опкод Line); при наличии LineTable он приоритетнее.</summary>
	public int Line { get; set; }
	/// <summary>Конструктор: при возврате вместо результата отдаётся This.</summary>
	public bool ReturnsThis { get; init; }
	/// <summary>Для async-функции: её задача (корутина).</summary>
	public KTask? Task { get; set; }

	/// <summary>Строка исходника для смещения pc (по LineTable либо по последнему Line).</summary>
	public int LineAt(int pc)
	{
		var lt = Def.LineTable;
		if (lt.Count == 0) return Line;
		int line = 0;
		int lo = 0, hi = lt.Count - 1;
		while (lo <= hi)
		{
			int mid = (lo + hi) >> 1;
			if (lt[mid].Offset <= pc) { line = lt[mid].Line; lo = mid + 1; } else hi = mid - 1;
		}
		return line == 0 ? Line : line;
	}
}
