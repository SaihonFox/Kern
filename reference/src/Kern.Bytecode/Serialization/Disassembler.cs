using System.Text;

namespace Kern.Bytecode.Serialization;

/// <summary>Читаемый листинг <see cref="KbcModule"/> (для <c>kernc dis</c> и отладки).</summary>
public static class Disassembler
{
	public static string Disassemble(KbcModule m)
	{
		var sb = new StringBuilder();
		sb.AppendLine($"; модуль {m.Name}  (KBC v{KbcModule.Version})");
		foreach (var r in m.References) sb.AppendLine($"; ссылка {r}");
		if (m.EntryPoint >= 0) sb.AppendLine($"; точка входа: #{m.EntryPoint} {FuncName(m, m.EntryPoint)}{(m.EntryIsAsync ? " async" : "")}{(m.EntryTakesArgs ? " (args)" : "")}");
		if (m.StaticInit.Count > 0) sb.AppendLine($"; static init: {string.Join(", ", m.StaticInit.Select(i => "#" + i))}");
		if (m.Tests.Count > 0) sb.AppendLine($"; тесты: {string.Join(", ", m.Tests.Select(i => "#" + i))}");
		sb.AppendLine();

		for (int i = 0; i < m.Types.Count; i++)
		{
			var t = m.Types[i];
			sb.Append($"type #{i} {t.Kind} {t.FullName}");
			if (t.GenericParams.Count > 0) sb.Append($"[{string.Join(", ", t.GenericParams.Select(g => g.Name))}]");
			if (t.Parents.Count > 0) sb.Append($" : {string.Join(", ", t.Parents.Select(Sig))}");
			sb.AppendLine();
			foreach (var f in t.Fields) sb.AppendLine($"    field {f.Name}: {Sig(f.Type)} slot={f.Slot} [{f.Flags}]");
			foreach (var p in t.Properties) sb.AppendLine($"    prop {p.Name}: {Sig(p.Type)} get=#{p.Getter} set=#{p.Setter} init=#{p.Init}");
			if (t.Kind == TypeKind.Enum)
				for (int k = 0; k < t.EnumNames.Count; k++) sb.AppendLine($"    case {t.EnumNames[k]} = {t.EnumValues[k]}");
			if (t.Kind == TypeKind.Union) sb.AppendLine($"    members: {string.Join(" | ", t.UnionMembers.Select(Sig))}");
			if (t.Ctors.Count > 0) sb.AppendLine($"    ctors: {string.Join(", ", t.Ctors.Select(c => "#" + c))}");
			if (t.Methods.Count > 0) sb.AppendLine($"    methods: {string.Join(", ", t.Methods.Select(c => "#" + c))}");
		}
		if (m.Types.Count > 0) sb.AppendLine();

		for (int i = 0; i < m.Globals.Count; i++)
			sb.AppendLine($"global #{i} {m.Globals[i].Name}: {Sig(m.Globals[i].Type)}");
		if (m.Globals.Count > 0) sb.AppendLine();

		for (int i = 0; i < m.Functions.Count; i++) Function(sb, m, i);
		return sb.ToString();
	}

	private static void Function(StringBuilder sb, KbcModule m, int index)
	{
		var f = m.Functions[index];
		sb.Append($"func #{index} {FuncName(m, index)}");
		if (f.GenericParams.Count > 0) sb.Append($"[{string.Join(", ", f.GenericParams.Select(g => g.Name))}]");
		sb.Append($"({string.Join(", ", f.Params.Select(p => $"{p.Name}: {Sig(p.Type)}{(p.IsVariadic ? "..." : "")}{(p.HasDefault ? " = …" : "")}"))}) -> {Sig(f.Return)}");
		sb.AppendLine($"  [{f.Flags}] locals={f.LocalCount} stack={f.MaxStack}{(f.Overrides >= 0 ? $" overrides=#{f.Overrides}" : "")}{(f.NativeKey != null ? $" native={f.NativeKey}" : "")}");

		var code = f.Code;
		int ip = 0;
		while (ip < code.Length)
		{
			var op = (OpCode)code[ip];
			int at = ip++;
			int n = OpCodeInfo.OperandCount(op);
			int[] ops;
			if (op == OpCode.Switch)
			{
				int cnt = ip < code.Length ? code[ip] : 0;
				int total = Math.Min(cnt + 1, code.Length - ip);
				ops = code.AsSpan(ip, total).ToArray();
				ip += total;
			}
			else
			{
				n = Math.Min(n, code.Length - ip);
				ops = code.AsSpan(ip, n).ToArray();
				ip += n;
			}
			sb.Append($"  {at,5}: {op,-18}");
			sb.AppendLine(Operands(m, op, ops));
		}
		sb.AppendLine();
	}

	private static string Operands(KbcModule m, OpCode op, int[] a)
	{
		if (a.Length == 0) return "";
		try
		{
			switch (op)
			{
				case OpCode.LdStr: return $"{a[0]} \"{Esc(m.Strings[a[0]])}\"";
				case OpCode.LdConst: return $"{a[0]} ({Const(m.Constants[a[0]])})";
				case OpCode.Call or OpCode.CallVirt or OpCode.CallNative:
					return $"#{a[0]} {FuncName(m, a[0])} typeArgs={TypeArgs(m, a[1])} argc={a[2]}";
				case OpCode.LdFunc or OpCode.BindFunc: return $"#{a[0]} {FuncName(m, a[0])}";
				case OpCode.MakeClosure: return $"#{a[0]} {FuncName(m, a[0])} captures={a[1]}";
				case OpCode.NewObj or OpCode.NewUnit:
					return $"type #{a[0]} {TypeName(m, a[0])} ctor=#{a[1]} typeArgs={TypeArgs(m, a[2])} argc={a[3]}";
				case OpCode.IsType or OpCode.NotIsType or OpCode.CastType or OpCode.TypeOf:
					return $"{a[0]} {SigAt(m, a[0])}";
				case OpCode.LdEnum: return $"{TypeName(m, a[0])}.{(a[1] < m.Types[a[0]].EnumNames.Count ? m.Types[a[0]].EnumNames[a[1]] : a[1].ToString())}";
				case OpCode.GetProp or OpCode.SetProp or OpCode.BindMethod: return $"{a[0]} \"{m.Strings[a[0]]}\"";
				case OpCode.Jmp or OpCode.JmpIf or OpCode.JmpIfNot or OpCode.JmpIfNil or OpCode.JmpIfNotNilOrNone
					or OpCode.JmpIfNone or OpCode.IterNext: return $"-> {a[0]}";
				case OpCode.Switch: return $"n={a[0]} [{string.Join(", ", a.Skip(1))}]";
				default: return string.Join(", ", a);
			}
		}
		catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
		{
			return string.Join(", ", a) + "  ; ⚠ операнд вне диапазона";
		}
	}

	private static string FuncName(KbcModule m, int i) =>
		i < 0 || i >= m.Functions.Count ? $"<bad #{i}>" :
		m.Functions[i].Owner >= 0 && m.Functions[i].Owner < m.Types.Count
			? $"{m.Types[m.Functions[i].Owner].FullName}.{m.Functions[i].Name}"
			: m.Functions[i].Name;

	private static string TypeName(KbcModule m, int i) => i >= 0 && i < m.Types.Count ? m.Types[i].FullName : $"<bad #{i}>";

	private static string SigAt(KbcModule m, int i) => i >= 0 && i < m.TypeSigs.Count ? Sig(m.TypeSigs[i]) : $"<bad sig {i}>";

	private static string TypeArgs(KbcModule m, int i) =>
		i < 0 ? "[]" : i < m.TypeSigLists.Count ? "[" + string.Join(", ", m.TypeSigLists[i].Select(s => SigAt(m, s))) + "]" : $"<bad {i}>";

	private static string Const(object? o) => o switch
	{
		null => "nil",
		string s => $"\"{Esc(s)}\"",
		char c => $"'{c}'",
		TypeSig t => "typeof " + Sig(t),
		_ => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) + ":" + o.GetType().Name,
	};

	private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t").Replace("\"", "\\\"");

	public static string Sig(TypeSig t) => t switch
	{
		TypeSig.Prim p => p.Kind.ToString().ToLowerInvariant(),
		TypeSig.Named n => n.Args.Count == 0 ? n.FullName : $"{n.FullName}<{string.Join(", ", n.Args.Select(Sig))}>",
		TypeSig.Param p => p.Name,
		TypeSig.Func f => $"({string.Join(", ", f.Params.Select(Sig))}) -> {Sig(f.Return)}",
		TypeSig.Tuple tp => $"({string.Join(", ", tp.Items.Select((x, i) => tp.Names?[i] is { } nm ? $"{nm}: {Sig(x)}" : Sig(x)))})",
		TypeSig.Nullable nl => Sig(nl.Inner) + "?",
		TypeSig.Union u => string.Join(" | ", u.Members.Select(Sig)),
		_ => t.ToString() ?? "?",
	};
}
