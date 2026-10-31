using Kern.Bytecode;
using Kern.Bytecode.Serialization;

namespace Kern.Cli.Kbc;

/// <summary>
/// Компоновка .kbc-библиотеки в программу. Потребитель уже скомпилирован вместе с интерфейсными исходниками библиотеки
/// (<see cref="KbcStubs"/>): в модуле есть типы и функции-заглушки с теми же сигнатурами и раскладкой полей.
/// Линкер сопоставляет функции библиотеки с заглушками по ключу (владелец, имя, флаги, типы параметров, порядковый номер),
/// подставляет в заглушки настоящий код и дописывает остальное (приватные функции, лямбды, глобальные) в конец,
/// переписывая индексы функций, типов, глобальных, строк, констант и подписей типов.
/// </summary>
public static class KbcLinker
{
	private const MemberFlags KeyFlags = MemberFlags.Static | MemberFlags.Getter | MemberFlags.Setter | MemberFlags.Init
		| MemberFlags.Operator | MemberFlags.Extension | MemberFlags.Ctor | MemberFlags.Lambda | MemberFlags.Generated | MemberFlags.Abstract;

	/// <summary>Подключить все библиотеки проекта (kern.toml `kbc = [...]`). Без библиотек ничего не делает.</summary>
	public static void LinkProject(KbcModule main, string? projectDir)
	{
		foreach (var lib in KbcConfig.Load(projectDir))
			Link(main, lib.Module);
	}

	public static void Link(KbcModule main, KbcModule lib)
	{
		var stubs = KbcStubs.Read(lib);
		if (stubs.Count == 0)
			throw new KbcException($"в .kbc `{lib.Name}` нет интерфейсных исходников: пересоберите библиотеку командой `kernc dll`");
		var libModules = stubs.Select(s => s.Module).ToHashSet();
		string prefix = $"{KbcStubs.PathPrefix}{lib.Name}/";

		// ---- типы
		var mainTypeByName = new Dictionary<string, int>();
		for (int i = 0; i < main.Types.Count; i++) mainTypeByName.TryAdd(main.Types[i].Name, i);
		var libTypeNames = lib.Types.Select(t => t.Name).ToHashSet();

		var typeMap = new int[lib.Types.Count];
		var appendedTypes = new List<(int LibIndex, TypeDef Def)>();
		for (int i = 0; i < lib.Types.Count; i++)
		{
			var lt = lib.Types[i];
			if (mainTypeByName.TryGetValue(lt.Name, out int mi))
			{
				CheckLayout(lt, main.Types[mi]);
				typeMap[i] = mi;
			}
			else
			{
				var clone = CloneType(lt);
				main.Types.Add(clone);
				typeMap[i] = main.Types.Count - 1;
				appendedTypes.Add((i, clone));
			}
		}

		// ---- функции
		var candidates = new Dictionary<string, Queue<int>>();
		for (int k = 0; k < main.Functions.Count; k++)
		{
			var f = main.Functions[k];
			if (!IsStub(main, f, prefix, libTypeNames, libModules)) continue;
			string key = Key(main, f);
			if (!candidates.TryGetValue(key, out var q)) candidates[key] = q = new Queue<int>();
			q.Enqueue(k);
		}

		var funcMap = new int[lib.Functions.Count];
		for (int k = 0; k < lib.Functions.Count; k++)
		{
			if (candidates.TryGetValue(Key(lib, lib.Functions[k]), out var q) && q.Count > 0)
				funcMap[k] = q.Dequeue();
			else
			{
				funcMap[k] = main.Functions.Count;
				main.Functions.Add(new FunctionDef { Name = lib.Functions[k].Name });
			}
		}

		// ---- глобальные: по имени и типу среди ещё не занятых, иначе в конец
		var globalMap = new int[lib.Globals.Count];
		var usedGlobals = new HashSet<int>();
		for (int g = 0; g < lib.Globals.Count; g++)
		{
			var lg = lib.Globals[g];
			int found = -1;
			for (int m = 0; m < main.Globals.Count && found < 0; m++)
				if (!usedGlobals.Contains(m) && main.Globals[m].Name == lg.Name && main.Globals[m].Type.Equals(lg.Type)) found = m;
			if (found < 0)
			{
				main.Globals.Add(new FieldDef { Name = lg.Name, Type = lg.Type, Flags = lg.Flags, Attributes = lg.Attributes, Slot = lg.Slot });
				found = main.Globals.Count - 1;
			}
			usedGlobals.Add(found);
			globalMap[g] = found;
		}

		var rel = new Relocator(lib, main, funcMap, typeMap, globalMap);

		// ---- наполнение функций
		for (int k = 0; k < lib.Functions.Count; k++)
		{
			var lf = lib.Functions[k];
			var target = main.Functions[funcMap[k]];
			target.Name = lf.Name;
			target.Owner = lf.Owner >= 0 ? typeMap[lf.Owner] : -1;
			target.Flags = lf.Flags;
			target.GenericParams = lf.GenericParams;
			target.Return = lf.Return;
			target.OpSymbol = lf.OpSymbol;
			target.ExtensionOf = lf.ExtensionOf;
			target.LocalCount = lf.LocalCount;
			target.MaxStack = lf.MaxStack;
			target.LineTable = lf.LineTable;
			target.SourceFile = lf.SourceFile;
			target.Attributes = lf.Attributes;
			target.Overrides = lf.Overrides >= 0 ? funcMap[lf.Overrides] : -1;
			target.NativeKey = lf.NativeKey;
			target.CaptureCount = lf.CaptureCount;
			target.Params = [.. lf.Params.Select(p => new ParamDef
			{
				Name = p.Name, Type = p.Type, HasDefault = p.HasDefault, IsVariadic = p.IsVariadic,
				DefaultConstant = p.DefaultConstant is null ? null : new ConstantArg { Name = p.DefaultConstant.Name, Value = p.DefaultConstant.Value },
				DefaultFunction = p.DefaultFunction >= 0 ? funcMap[p.DefaultFunction] : -1,
			})];
			target.Code = rel.Code(lf.Code);
		}

		// ---- добавленные типы: ссылки на функции
		foreach (var (libIndex, def) in appendedTypes)
		{
			def.Methods = [.. def.Methods.Select(i => funcMap[i])];
			def.Ctors = [.. def.Ctors.Select(i => funcMap[i])];
			def.ParentInit = [.. def.ParentInit.Select(i => i >= 0 ? funcMap[i] : i)];
			foreach (var p in def.Properties)
			{
				if (p.Getter >= 0) p.Getter = funcMap[p.Getter];
				if (p.Setter >= 0) p.Setter = funcMap[p.Setter];
				if (p.Init >= 0) p.Init = funcMap[p.Init];
			}
		}

		// ---- статическая инициализация библиотеки идёт раньше кода программы
		var inits = new List<int>();
		foreach (int s in lib.StaticInit)
			if (!main.StaticInit.Contains(funcMap[s]) && !inits.Contains(funcMap[s])) inits.Add(funcMap[s]);
		main.StaticInit.InsertRange(0, inits);
	}

	// ---------------------------------------------------------------- сопоставление

	private static bool IsStub(KbcModule m, FunctionDef f, string prefix, HashSet<string> libTypeNames, HashSet<string> libModules)
	{
		if (f.SourceFile is { } sf)
		{
			if (sf.StartsWith(prefix, StringComparison.Ordinal)) return true;
			if (f.Name == "<static-init>" && libModules.Contains(sf)) return true;
			return false;
		}
		return f.Owner >= 0 && f.Owner < m.Types.Count && libTypeNames.Contains(m.Types[f.Owner].Name);
	}

	private static string Key(KbcModule m, FunctionDef f)
	{
		string owner = f.Owner >= 0 && f.Owner < m.Types.Count ? m.Types[f.Owner].Name : "";
		string ps = string.Join(",", f.Params.Select(p => Disassembler.Sig(p.Type)));
		string ext = f.ExtensionOf is null ? "" : Disassembler.Sig(f.ExtensionOf);
		string file = f.Name == "<static-init>" ? f.SourceFile ?? "" : "";
		return $"{owner}|{f.Name}|{(int)(f.Flags & KeyFlags)}|{f.OpSymbol}|{f.GenericParams.Count}|{ps}|{ext}|{file}";
	}

	/// <summary>Раскладка полей заглушки должна совпадать с библиотечной: иначе слоты в коде потребителя и библиотеки разойдутся.</summary>
	private static void CheckLayout(TypeDef lib, TypeDef stub)
	{
		var a = lib.Fields.Where(f => !f.Flags.HasFlag(MemberFlags.Static)).Select(f => (f.Name, f.Slot)).ToList();
		var b = stub.Fields.Where(f => !f.Flags.HasFlag(MemberFlags.Static)).Select(f => (f.Name, f.Slot)).ToList();
		if (!a.SequenceEqual(b))
			throw new KbcException($"тип `{lib.Name}`: раскладка полей в .kbc ({string.Join(", ", a.Select(x => x.Name))}) "
				+ $"не совпадает с интерфейсом ({string.Join(", ", b.Select(x => x.Name))}); пересоберите библиотеку и проект");
	}

	private static TypeDef CloneType(TypeDef t) => new()
	{
		Name = t.Name, FullName = t.FullName, Kind = t.Kind, Flags = t.Flags,
		GenericParams = t.GenericParams, Parents = [.. t.Parents], ParentInit = [.. t.ParentInit],
		Fields = [.. t.Fields.Select(f => new FieldDef { Name = f.Name, Type = f.Type, Flags = f.Flags, Attributes = f.Attributes, Slot = f.Slot })],
		Properties = [.. t.Properties.Select(p => new PropertyDef
		{
			Name = p.Name, Type = p.Type, Flags = p.Flags, Getter = p.Getter, Setter = p.Setter, Init = p.Init,
			BackingField = p.BackingField, Attributes = p.Attributes,
		})],
		Methods = [.. t.Methods], Ctors = [.. t.Ctors], Attributes = t.Attributes,
		UnionMembers = t.UnionMembers, EnumNames = t.EnumNames, EnumValues = t.EnumValues,
		ParentChoices = t.ParentChoices, NativeType = t.NativeType,
	};

	// ---------------------------------------------------------------- переписывание операндов

	private sealed class Relocator(KbcModule lib, KbcModule main, int[] funcs, int[] types, int[] globals)
	{
		private readonly Dictionary<int, int> _strings = [];
		private readonly Dictionary<int, int> _consts = [];
		private readonly Dictionary<int, int> _sigs = [];
		private readonly Dictionary<int, int> _lists = [];

		private int F(int i) => i < 0 ? i : funcs[i];
		private int T(int i) => i < 0 ? i : types[i];
		private int G(int i) => i < 0 ? i : globals[i];

		private int Str(int i) => i < 0 ? i : _strings.TryGetValue(i, out int r) ? r : _strings[i] = main.Intern(lib.Strings[i]);
		private int Const(int i) => i < 0 ? i : _consts.TryGetValue(i, out int r) ? r : _consts[i] = main.AddConstant(lib.Constants[i]);
		private int Sig(int i) => i < 0 ? i : _sigs.TryGetValue(i, out int r) ? r : _sigs[i] = main.Intern(lib.TypeSigs[i]);
		private int List(int i) => i < 0 ? i : _lists.TryGetValue(i, out int r) ? r : _lists[i] = main.InternList(lib.TypeSigLists[i].Select(s => lib.TypeSigs[s]));

		public int[] Code(int[] src)
		{
			var code = (int[])src.Clone();
			int ip = 0;
			while (ip < code.Length)
			{
				var op = (OpCode)code[ip];
				int n = OpCodeInfo.OperandCount(op);
				if (op == OpCode.Switch) n = (ip + 1 < code.Length ? code[ip + 1] : 0) + 1; // n, цели переходов — не индексы пулов
				int a = ip + 1;
				if (a + n > code.Length) break; // обрезанный код: оставляем как есть
				switch (op)
				{
					case OpCode.LdConst: code[a] = Const(code[a]); break;
					case OpCode.LdStr or OpCode.GetProp or OpCode.SetProp or OpCode.BindMethod or OpCode.Format:
						code[a] = Str(code[a]); break;
					case OpCode.CallOp or OpCode.CallDotnet: code[a] = Str(code[a]); break;
					case OpCode.CallNative: code[a] = Str(code[a]); code[a + 2] = List(code[a + 2]); break;
					case OpCode.Call or OpCode.CallVirt: code[a] = F(code[a]); code[a + 2] = List(code[a + 2]); break;
					case OpCode.LdFunc or OpCode.BindFunc or OpCode.MakeClosure: code[a] = F(code[a]); break;
					case OpCode.NewObj or OpCode.NewUnit:
						code[a] = T(code[a]); code[a + 1] = F(code[a + 1]); code[a + 3] = List(code[a + 3]); break;
					case OpCode.NewList or OpCode.NewArray or OpCode.NewDict or OpCode.NewTuple: code[a] = Sig(code[a]); break;
					case OpCode.IsType or OpCode.NotIsType or OpCode.CastType or OpCode.TypeOf or OpCode.MatchType:
						code[a] = Sig(code[a]); break;
					case OpCode.LdEnum: code[a] = T(code[a]); break;
					case OpCode.LdStatic or OpCode.StStatic: code[a] = G(code[a]); break;
				}
				ip = a + n;
			}
			return code;
		}
	}
}
