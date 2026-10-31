namespace Kern.Bytecode.Layout;

/// <summary>
/// Раскладка полей по слотам. Правило: все поля предков и самого типа получают РАЗНЫЕ слоты,
/// слот поля один и тот же во всех наследниках (поэтому LdField/StField работают при любой глубине и
/// множественном наследовании без пересчёта). Для одиночного наследования — обычная раскладка «родитель, затем свои».
/// Идемпотентна: компилятор вызывает её перед генерацией кода, загрузчик VM — повторно.
/// </summary>
public static class FieldLayout
{
	/// <summary>Назначает FieldDef.Slot для всего модуля и возвращает число слотов для каждого типа.</summary>
	public static int[] Assign(KbcModule m, ModuleIndex? index = null)
	{
		index ??= new ModuleIndex(m);
		int n = m.Types.Count;
		var assigned = new HashSet<FieldDef>(ReferenceEqualityComparer.Instance);
		// типы в порядке «предки раньше потомков»
		var order = Enumerable.Range(0, n).OrderBy(i => index.Ancestors[i].Length).ToArray();
		// потомки: для типа T — все U, у которых T среди предков (и сам T)
		var descendants = new List<int>[n];
		for (int i = 0; i < n; i++) descendants[i] = [i];
		for (int u = 0; u < n; u++) foreach (int a in index.Ancestors[u]) descendants[a].Add(u);

		foreach (int t in order)
		{
			foreach (var f in m.Types[t].Fields)
			{
				if (f.Flags.HasFlag(MemberFlags.Static)) continue;
				var forbidden = new HashSet<int>();
				foreach (int u in descendants[t])
				{
					CollectSlots(u, m, index, assigned, forbidden, f);
				}
				int slot = 0;
				while (forbidden.Contains(slot)) slot++;
				f.Slot = slot;
				assigned.Add(f);
			}
		}
		var counts = new int[n];
		for (int u = 0; u < n; u++)
		{
			int max = 0;
			foreach (int t in index.Ancestors[u].Append(u))
				foreach (var f in m.Types[t].Fields)
					if (!f.Flags.HasFlag(MemberFlags.Static)) max = Math.Max(max, f.Slot + 1);
			counts[u] = max;
		}
		return counts;
	}

	private static void CollectSlots(int type, KbcModule m, ModuleIndex index, HashSet<FieldDef> assigned, HashSet<int> into, FieldDef skip)
	{
		foreach (int t in index.Ancestors[type].Append(type))
			foreach (var g in m.Types[t].Fields)
				if (!ReferenceEquals(g, skip) && assigned.Contains(g)) into.Add(g.Slot);
	}
}
