using Kern.Bytecode;
using Kern.Bytecode.Layout;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>Превращает KbcModule в рантайм-таблицы: раскладка полей, vtable, операторы, константы, статические поля.</summary>
public static class ModuleLoader
{
	public static KModuleInfo Load(KbcModule m)
	{
		var info = new KModuleInfo(m) { Index = new ModuleIndex(m) };
		info.Types = new TypeFactory(info);
		int[] counts = FieldLayout.Assign(m, info.Index);

		info.Classes = new KClassInfo[m.Types.Count];
		for (int i = 0; i < m.Types.Count; i++) info.Classes[i] = new KClassInfo(m.Types[i], i);

		// порядок «предки раньше потомков»
		foreach (int i in Enumerable.Range(0, m.Types.Count).OrderBy(x => info.Index.Ancestors[x].Length))
			BuildClass(info, i, counts[i]);

		// операторы
		for (int f = 0; f < m.Functions.Count; f++)
		{
			var fd = m.Functions[f];
			if (fd.Flags.HasFlag(MemberFlags.Operator) && fd.OpSymbol is { } sym)
			{
				if (!info.Operators.TryGetValue(sym, out var list)) info.Operators[sym] = list = [];
				list.Add(f);
			}
		}

		// константы
		info.ConstValues = new KValue[m.Constants.Count];
		for (int i = 0; i < m.Constants.Count; i++)
			info.ConstValues[i] = m.Constants[i] switch
			{
				TypeSig => KValue.None, // разрешается во фрейме (зависит от T)
				null => KValue.Nil,
				var o => Num.FromClr(o),
			};

		info.Statics = new KValue[m.Globals.Count];
		Array.Fill(info.Statics, KValue.None);
		return info;
	}

	/// <summary>
	/// Подключает к уже загруженному модулю новый тип без родителей (рефлексия: `reflect.define_type`). Поля получают слоты 0..n-1.
	/// Возвращает индекс типа в Module.Types.
	/// </summary>
	public static int AddType(KModuleInfo info, TypeDef def)
	{
		lock (info)
		{
			if (info.Index.TypeByName.ContainsKey(def.FullName))
				throw new KernPanic($"тип '{def.FullName}' уже существует");
			int slot = 0;
			foreach (var f in def.Fields)
				if (!f.Flags.HasFlag(MemberFlags.Static)) f.Slot = slot++;
			info.Module.Types.Add(def);
			int index = info.Index.AddType(def);
			info.Classes = [.. info.Classes, new KClassInfo(def, index)];
			BuildClass(info, index, slot);
			return index;
		}
	}

	private static void BuildClass(KModuleInfo info, int i, int fieldCount)
	{
		var m = info.Module;
		var ci = info.Classes[i];
		var def = ci.Def;
		var idx = info.Index;
		ci.Parents = idx.Parents[i];
		ci.Ancestors = idx.Ancestors[i];
		ci.AncestorSet = [.. ci.Ancestors];
		ci.FieldCount = fieldCount;

		// поля: дальние предки → свои
		var all = new List<FieldDef>();
		foreach (int a in Enumerable.Reverse(ci.Ancestors).Append(i))
			foreach (var f in m.Types[a].Fields)
				if (!f.Flags.HasFlag(MemberFlags.Static) && !all.Contains(f)) all.Add(f);
		ci.AllFields = [.. all];
		foreach (var f in all) ci.FieldsByName[f.Name] = f;
		foreach (int a in Enumerable.Reverse(ci.Ancestors).Append(i))
			foreach (var p in m.Types[a].Properties) ci.PropsByName[p.Name] = p;

		// методы по имени: свои, затем унаследованные
		foreach (int f in def.Methods) Add(ci.MethodsByName, m.Functions[f].Name, f);
		foreach (int p in ci.Parents)
			foreach (var kv in info.Classes[p].MethodsByName)
				foreach (int f in kv.Value) if (!(ci.MethodsByName.TryGetValue(kv.Key, out var l) && l.Contains(f))) Add(ci.MethodsByName, kv.Key, f);

		// vtable: от родителей (первый выигрывает), затем свои переопределения
		foreach (int p in ci.Parents)
			foreach (var kv in info.Classes[p].Vtable)
				if (!ci.Vtable.TryGetValue(kv.Key, out int cur) || IsAbstract(m, cur) && !IsAbstract(m, kv.Value))
					ci.Vtable[kv.Key] = kv.Value;
		foreach (int f in def.Methods)
		{
			var fd = m.Functions[f];
			if (fd.Flags.HasFlag(MemberFlags.Static) || fd.Flags.HasFlag(MemberFlags.Ctor)) continue;
			int root = idx.Roots[f];
			bool abs = fd.Flags.HasFlag(MemberFlags.Abstract);
			if (abs && ci.Vtable.TryGetValue(root, out int existing) && !IsAbstract(m, existing)) continue;
			ci.Vtable[root] = f;
		}

		// $[A].member: выбранная реализация родителя удовлетворяет одноимённым абстрактным членам
		foreach (var (member, parentIdx) in def.ParentChoices)
		{
			if (parentIdx < 0 || parentIdx >= ci.Parents.Length) continue;
			var pinfo = info.Classes[ci.Parents[parentIdx]];
			if (!pinfo.MethodsByName.TryGetValue(member, out var impls)) continue;
			int chosen = impls.FirstOrDefault(f => !IsAbstract(m, f), impls[0]);
			foreach (var key in ci.Vtable.Keys.ToArray())
				if (m.Functions[key].Name == member && IsAbstract(m, ci.Vtable[key])) ci.Vtable[key] = chosen;
		}

		// запасной вариант: абстрактный слот → одноимённая конкретная реализация с тем же числом параметров
		foreach (var key in ci.Vtable.Keys.ToArray())
		{
			int impl = ci.Vtable[key];
			if (!IsAbstract(m, impl) || def.Kind is TypeKind.AbstractClass or TypeKind.Interface) continue;
			var abs = m.Functions[impl];
			if (ci.MethodsByName.TryGetValue(abs.Name, out var cands))
				foreach (int c in cands)
					if (!IsAbstract(m, c) && m.Functions[c].Params.Count == abs.Params.Count) { ci.Vtable[key] = c; break; }
		}
	}

	private static bool IsAbstract(KbcModule m, int f) => m.Functions[f].Flags.HasFlag(MemberFlags.Abstract);

	private static void Add(Dictionary<string, List<int>> d, string name, int f)
	{
		if (!d.TryGetValue(name, out var l)) d[name] = l = [];
		l.Add(f);
	}
}
