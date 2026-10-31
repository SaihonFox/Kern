using Kern.Bytecode;

namespace Kern.Compiler.Optimization;

/// <summary>
/// Оптимизатор байткода (-O2). Работает над готовым кодом функции: раскладывает его в список инструкций со ссылками-переходами,
/// применяет локальные упрощения до неподвижной точки и собирает код заново (пересчитывая цели переходов и таблицу строк).
/// <list type="bullet">
/// <item>снятие «пустых» пар: чистая загрузка + <c>Pop</c> (в частности <c>LdVoid; Pop</c> после вызовов-операторов), <c>Dup; Pop</c>, <c>Nop</c>;</item>
/// <item><c>Jmp</c> на следующую инструкцию; цепочки <c>Jmp → Jmp</c> (прошивка переходов);</item>
/// <item><c>Not; JmpIf</c> → <c>JmpIfNot</c> и наоборот; условные переходы по константе (<c>LdTrue; JmpIf</c>);</item>
/// <item>недостижимый код после <c>Jmp</c>/<c>Ret</c>/<c>RetVoid</c>/<c>Panic</c> до ближайшей цели перехода;</item>
/// <item><c>LdLoc n; StLoc n</c> (запись переменной в саму себя).</item>
/// </list>
/// Инструкцию, на которую кто-то прыгает, никогда не сливают с предыдущей.
/// </summary>
public static class PeepholeOptimizer
{
	private sealed class Instr(OpCode op, int[] operands)
	{
		public OpCode Op = op;
		public int[] Operands = operands;
		public Instr?[] Targets = [];   // для переходов: адресаты (по порядку операндов-целей)
		public bool Dead;
		public int OldOffset;
		public int NewOffset;
	}

	public static void Optimize(FunctionDef f)
	{
		if (f.Code.Length == 0 || f.Flags.HasFlag(MemberFlags.Native)) return;
		var list = Decode(f.Code);
		if (list is null) return;

		bool changed;
		int rounds = 0;
		do
		{
			changed = false;
			changed |= RemoveNops(list);
			changed |= ThreadJumps(list);
			changed |= PeepPairs(list);
			changed |= RemoveJumpToNext(list);
			changed |= RemoveUnreachable(list);
		} while (changed && ++rounds < 16);

		Encode(f, list);
	}

	// ------------------------------------------------------------ разбор

	private static int[] TargetSlots(OpCode op, int[] ops) => op switch
	{
		OpCode.Jmp or OpCode.JmpIf or OpCode.JmpIfNot or OpCode.JmpIfNil or OpCode.JmpIfNotNilOrNone or OpCode.JmpIfNone
			or OpCode.IterNext or OpCode.MatchEq => [0],
		OpCode.MatchType => [1],
		OpCode.Switch => [.. Enumerable.Range(1, ops[0])],
		_ => [],
	};

	private static List<Instr>? Decode(int[] code)
	{
		var list = new List<Instr>();
		var byOffset = new Dictionary<int, Instr>();
		for (int pc = 0; pc < code.Length;)
		{
			var op = (OpCode)code[pc];
			int count = op == OpCode.Switch ? 1 + code[pc + 1] : OpCodeInfo.OperandCount(op);
			if (count < 0 || pc + 1 + count > code.Length) return null;
			var ins = new Instr(op, code[(pc + 1)..(pc + 1 + count)]) { OldOffset = pc };
			list.Add(ins);
			byOffset[pc] = ins;
			pc += 1 + count;
		}
		// цель == длина кода — «конец функции»; без инструкции-адресата оптимизировать небезопасно
		foreach (var ins in list)
		{
			var slots = TargetSlots(ins.Op, ins.Operands);
			ins.Targets = new Instr?[slots.Length];
			for (int i = 0; i < slots.Length; i++)
			{
				if (!byOffset.TryGetValue(ins.Operands[slots[i]], out var t)) return null;
				ins.Targets[i] = t;
			}
		}
		return list;
	}

	private static HashSet<Instr> JumpTargets(List<Instr> list)
	{
		var set = new HashSet<Instr>(ReferenceEqualityComparer.Instance);
		foreach (var i in list)
			if (!i.Dead)
				foreach (var t in i.Targets)
					if (t is not null) set.Add(Live(list, t));
		return set;
	}

	/// <summary>Первая живая инструкция начиная с t (удалённые «проваливаются» дальше).</summary>
	private static Instr Live(List<Instr> list, Instr t)
	{
		if (!t.Dead) return t;
		int i = list.IndexOf(t);
		while (i < list.Count && list[i].Dead) i++;
		return i < list.Count ? list[i] : t;
	}

	private static List<Instr> Alive(List<Instr> list) => list.Where(i => !i.Dead).ToList();

	// ------------------------------------------------------------ проходы

	private static bool RemoveNops(List<Instr> list)
	{
		bool ch = false;
		foreach (var i in list)
			if (!i.Dead && i.Op is OpCode.Nop or OpCode.Line) { i.Dead = true; ch = true; }
		return ch;
	}

	private static bool IsPurePush(OpCode op) => op is OpCode.LdConst or OpCode.LdNil or OpCode.LdNone or OpCode.LdTrue
		or OpCode.LdFalse or OpCode.LdVoid or OpCode.LdI32 or OpCode.LdStr or OpCode.LdLoc or OpCode.LdArg or OpCode.LdThis or OpCode.Dup;

	private static bool PeepPairs(List<Instr> list)
	{
		bool ch = false;
		var targets = JumpTargets(list);
		var live = Alive(list);
		for (int k = 0; k + 1 < live.Count; k++)
		{
			var a = live[k];
			var b = live[k + 1];
			if (a.Dead || b.Dead || targets.Contains(b)) continue;

			// чистая загрузка + Pop
			if (b.Op == OpCode.Pop && IsPurePush(a.Op)) { a.Dead = b.Dead = true; ch = true; continue; }

			// Not + условный переход
			if (a.Op == OpCode.Not && b.Op is OpCode.JmpIf or OpCode.JmpIfNot)
			{
				b.Op = b.Op == OpCode.JmpIf ? OpCode.JmpIfNot : OpCode.JmpIf;
				a.Dead = true; ch = true; continue;
			}

			// переход по константе
			if (a.Op is OpCode.LdTrue or OpCode.LdFalse && b.Op is OpCode.JmpIf or OpCode.JmpIfNot)
			{
				bool value = a.Op == OpCode.LdTrue;
				bool taken = b.Op == OpCode.JmpIf ? value : !value;
				a.Dead = true;
				if (taken) { b.Op = OpCode.Jmp; }
				else b.Dead = true;
				ch = true;
				continue;
			}

			// загрузка и тут же запись того же слота
			if (a.Op == OpCode.LdLoc && b.Op == OpCode.StLoc && a.Operands[0] == b.Operands[0])
			{ a.Dead = b.Dead = true; ch = true; }
		}
		return ch;
	}

	private static bool ThreadJumps(List<Instr> list)
	{
		bool ch = false;
		foreach (var i in list)
		{
			if (i.Dead) continue;
			for (int s = 0; s < i.Targets.Length; s++)
			{
				var t = i.Targets[s];
				if (t is null) continue;
				var cur = Live(list, t);
				for (int hops = 0; hops < 8 && cur.Op == OpCode.Jmp && !cur.Dead && cur.Targets[0] is { } next; hops++)
				{
					var n = Live(list, next);
					if (n == cur) break;
					cur = n;
				}
				if (!ReferenceEquals(cur, t)) { i.Targets[s] = cur; ch = true; }
			}
		}
		return ch;
	}

	private static bool RemoveJumpToNext(List<Instr> list)
	{
		bool ch = false;
		var live = Alive(list);
		for (int k = 0; k + 1 < live.Count; k++)
			if (live[k].Op == OpCode.Jmp && ReferenceEquals(Live(list, live[k].Targets[0]!), live[k + 1]))
			{ live[k].Dead = true; ch = true; }
		return ch;
	}

	private static bool RemoveUnreachable(List<Instr> list)
	{
		bool ch = false;
		var targets = JumpTargets(list);
		var live = Alive(list);
		for (int k = 0; k < live.Count; k++)
		{
			if (live[k].Op is not (OpCode.Jmp or OpCode.Ret or OpCode.RetVoid or OpCode.Panic)) continue;
			for (int j = k + 1; j < live.Count && !targets.Contains(live[j]); j++)
			{ live[j].Dead = true; ch = true; }
		}
		return ch;
	}

	// ------------------------------------------------------------ сборка

	private static void Encode(FunctionDef f, List<Instr> list)
	{
		var live = Alive(list);
		if (live.Count == 0) return;

		int pc = 0;
		foreach (var i in live)
		{
			i.NewOffset = pc;
			pc += 1 + i.Operands.Length;
		}
		int endOffset = pc;

		int OffsetOf(Instr t)
		{
			var l = Live(list, t);
			return l.Dead ? endOffset : l.NewOffset;
		}

		var code = new int[pc];
		int p = 0;
		foreach (var i in live)
		{
			var slots = TargetSlots(i.Op, i.Operands);
			// перед записью: у переходов, превращённых из JmpIf в Jmp, операнд тот же — слоты совпадают
			for (int s = 0; s < slots.Length; s++) i.Operands[slots[s]] = OffsetOf(i.Targets[s]!);
			code[p++] = (int)i.Op;
			foreach (int o in i.Operands) code[p++] = o;
		}

		// таблица строк: старое смещение → смещение первой живой инструкции не раньше него
		var table = new List<(int Offset, int Line)>();
		foreach (var (off, line) in f.LineTable)
		{
			var target = list.FirstOrDefault(x => x.OldOffset >= off && !x.Dead);
			int newOff = target?.NewOffset ?? endOffset;
			if (table.Count > 0 && table[^1].Offset == newOff) table[^1] = (newOff, line);
			else if (table.Count == 0 || table[^1].Line != line) table.Add((newOff, line));
		}

		f.Code = code;
		f.LineTable = table;
		f.MaxStack = live.Count + 8;
	}
}
