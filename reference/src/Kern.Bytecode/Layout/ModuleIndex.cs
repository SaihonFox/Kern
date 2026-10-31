namespace Kern.Bytecode.Layout;

/// <summary>Вспомогательные индексы модуля: имя типа → индекс TypeDef, предки, корни виртуальных методов.</summary>
public sealed class ModuleIndex
{
	public KbcModule Module { get; }
	public Dictionary<string, int> TypeByName { get; } = [];
	/// <summary>Прямые родители (индексы TypeDef; внешние/неизвестные пропускаются).</summary>
	public int[][] Parents { get; private set; }
	/// <summary>Все предки (транзитивно, без самого типа), ближние первыми.</summary>
	public int[][] Ancestors { get; private set; }
	/// <summary>Корень цепочки Overrides для каждой функции (сама функция, если она ничего не переопределяет).</summary>
	public int[] Roots { get; private set; }

	public ModuleIndex(KbcModule m)
	{
		Module = m;
		for (int i = 0; i < m.Types.Count; i++) TypeByName[m.Types[i].FullName] = i;
		Parents = new int[m.Types.Count][];
		for (int i = 0; i < m.Types.Count; i++)
		{
			var list = new List<int>();
			foreach (var p in m.Types[i].Parents)
				if (p is TypeSig.Named n && TypeByName.TryGetValue(n.FullName, out int pi) && pi != i) list.Add(pi);
			Parents[i] = [.. list];
		}
		Ancestors = new int[m.Types.Count][];
		for (int i = 0; i < m.Types.Count; i++)
		{
			var seen = new HashSet<int>();
			var order = new List<int>();
			var queue = new Queue<int>(Parents[i]);
			while (queue.Count > 0)
			{
				int t = queue.Dequeue();
				if (t == i || !seen.Add(t)) continue;
				order.Add(t);
				foreach (var pp in Parents[t]) queue.Enqueue(pp);
			}
			Ancestors[i] = [.. order];
		}
		Roots = new int[m.Functions.Count];
		for (int f = 0; f < Roots.Length; f++)
		{
			int cur = f, guard = 0;
			while (m.Functions[cur].Overrides is >= 0 and var o && o < Roots.Length && guard++ < 64) cur = o;
			Roots[f] = cur;
		}
	}

	/// <summary>
	/// Регистрирует тип, добавленный в модуль уже ПОСЛЕ загрузки (динамические типы рефлексии): без родителей, функций не добавляет.
	/// Вызывающий сам добавляет TypeDef в Module.Types; возвращается его индекс.
	/// </summary>
	public int AddType(TypeDef def)
	{
		int index = Module.Types.IndexOf(def);
		TypeByName[def.FullName] = index;
		Parents = [.. Parents, []];
		Ancestors = [.. Ancestors, []];
		return index;
	}

	public bool IsSubtype(int type, int ancestor) => type == ancestor || Array.IndexOf(Ancestors[type], ancestor) >= 0;
}
