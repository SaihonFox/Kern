using System.Text;
using Kern.Bytecode;
using Kern.Vm.Async;
using Kern.Vm.Execution;

namespace Kern.Vm.Values;

/// <summary>Структурное равенство, хэш и строковое представление значений VM.</summary>
public static class ValueOps
{
	private const int MaxDepth = 8;

	// ---------- равенство ----------

	public static bool StructuralEquals(KValue a, KValue b)
	{
		if (a.IsNumber && b.IsNumber)
		{
			if (a.IsInteger && b.IsInteger) return Num.Compare(a, b) == 0;
			return a.AsF64 == b.AsF64;
		}
		if (a.Kind != b.Kind) return false;
		switch (a.Kind)
		{
			case ValueKind.Str: return string.Equals(a.AsStr, (string)b.Ref!, StringComparison.Ordinal);
			case ValueKind.Ref: return ReferenceEquals(a.Ref, b.Ref) || RefStructuralEquals(a.Ref!, b.Ref!);
			default: return a.Bits == b.Bits;
		}
	}

	public static int StructuralHash(KValue v)
	{
		if (v.IsInteger) return v.Bits.GetHashCode();
		if (v.IsFloat)
		{
			double d = v.AsF64;
			return d == Math.Floor(d) && Math.Abs(d) < 9e18 ? ((long)d).GetHashCode() : d.GetHashCode();
		}
		return v.Kind switch
		{
			ValueKind.Str => v.AsStr.GetHashCode(),
			ValueKind.Ref => RefStructuralHash(v.Ref!, 0),
			_ => HashCode.Combine(v.Kind, v.Bits),
		};
	}

	private static bool IsValueLike(KObject o) => o.Type.Def.Kind is TypeKind.Unit or TypeKind.ImmutableUnit or TypeKind.Enum;

	private static bool RefStructuralEquals(object a, object b)
	{
		switch (a)
		{
			case KObject x when b is KObject y:
				if (!IsValueLike(x) || !ReferenceEquals(x.Type, y.Type)) return false;
				return SeqEquals(x.Fields, y.Fields);
			case KListObject x when b is KListObject y:
				if (x.Items.Count != y.Items.Count) return false;
				for (int i = 0; i < x.Items.Count; i++) if (!StructuralEquals(x.Items[i], y.Items[i])) return false;
				return true;
			case KArrayObject x when b is KArrayObject y: return SeqEquals(x.Items, y.Items);
			case KTupleObject x when b is KTupleObject y: return SeqEquals(x.Items, y.Items);
			case KDictObject x when b is KDictObject y:
				if (x.Items.Count != y.Items.Count) return false;
				foreach (var kv in x.Items)
					if (!y.Items.TryGetValue(kv.Key, out var ov) || !StructuralEquals(kv.Value, ov)) return false;
				return true;
			case KResultObject x when b is KResultObject y: return x.IsOk == y.IsOk && StructuralEquals(x.Value, y.Value);
			case KTypeObject x when b is KTypeObject y: return ReferenceEquals(x.Type, y.Type);
			case KRange x when b is KRange y:
				return x.Inclusive == y.Inclusive && StructuralEquals(x.Start, y.Start) && StructuralEquals(x.End, y.End) && StructuralEquals(x.Step, y.Step);
			case KNativeBox x when b is KNativeBox y: return Equals(x.Value, y.Value);
			default: return false;
		}
	}

	private static bool SeqEquals(KValue[] a, KValue[] b)
	{
		if (a.Length != b.Length) return false;
		for (int i = 0; i < a.Length; i++) if (!StructuralEquals(a[i], b[i])) return false;
		return true;
	}

	private static int SeqHash(IEnumerable<KValue> items, int depth)
	{
		var h = new HashCode();
		foreach (var v in items) h.Add(depth > MaxDepth ? 0 : StructuralHash(v));
		return h.ToHashCode();
	}

	private static int RefStructuralHash(object o, int depth) => o switch
	{
		KObject k when IsValueLike(k) => HashCode.Combine(k.Type.DefIndex, SeqHash(k.Fields, depth + 1)),
		KListObject l => SeqHash(l.Items, depth + 1),
		KArrayObject a => SeqHash(a.Items, depth + 1),
		KTupleObject t => SeqHash(t.Items, depth + 1),
		KDictObject d => d.Items.Count,
		KResultObject r => HashCode.Combine(r.IsOk, StructuralHash(r.Value)),
		KTypeObject t => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(t.Type),
		KNativeBox nb => nb.Value.GetHashCode(),
		_ => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o),
	};

	// ---------- строковое представление ----------

	/// <summary>to_str значения (верхний уровень: строки и символы без кавычек).</summary>
	public static string ToStr(KValue v) => ToStr(v, 0, false);

	/// <summary>Представление внутри коллекций: строки в кавычках.</summary>
	public static string Repr(KValue v) => ToStr(v, 0, true);

	internal static string ToStr(KValue v, int depth, bool quote)
	{
		switch (v.Kind)
		{
			case ValueKind.None: return "none";
			case ValueKind.Nil: return "nil";
			case ValueKind.Void: return "void";
			case ValueKind.Bool: return v.AsBool ? "true" : "false";
			case ValueKind.Char: return quote ? "'" + Escape(v.AsChar.ToString(), '\'') + "'" : v.AsChar.ToString();
			case ValueKind.Str: return quote ? "\"" + Escape(v.AsStr, '"') + "\"" : v.AsStr;
			case ValueKind.Ref: return RefToStr(v.Ref!, depth);
			default: return Num.ToStr(v);
		}
	}

	private static string Escape(string s, char q)
	{
		var sb = new StringBuilder();
		foreach (char c in s)
			sb.Append(c switch { '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\\' => "\\\\", _ when c == q => "\\" + q, _ => c.ToString() });
		return sb.ToString();
	}

	private static string Join(IEnumerable<KValue> items, int depth) =>
		string.Join(", ", items.Select(i => ToStr(i, depth + 1, true)));

	private static string RefToStr(object o, int depth)
	{
		if (depth > MaxDepth) return "...";
		switch (o)
		{
			case KObject k:
			{
				if (Interpreter.Current is { } vm && vm.TryUserToStr(k, out var s)) return s;
				var def = k.Type.Def;
				if (def.Kind == TypeKind.Enum)
				{
					long val = k.Fields.Length > 0 ? k.Fields[0].Bits : 0;
					int idx = def.EnumValues.IndexOf(val);
					return idx >= 0 ? $"{def.Name}.{def.EnumNames[idx]}" : $"{def.Name}({val})";
				}
				var sb = new StringBuilder(def.Name).Append('(');
				bool first = true;
				foreach (var f in AllFields(k))
				{
					if (!first) sb.Append(", ");
					first = false;
					sb.Append(f.Name).Append('=').Append(ToStr(k.Fields[f.Slot], depth + 1, true));
				}
				return sb.Append(')').ToString();
			}
			case KListObject l: return "[" + Join(l.Items, depth) + "]";
			case KArrayObject a: return "[" + Join(a.Items, depth) + "]";
			case KDictObject d:
				if (d.Items.Count == 0) return "[:]";
				return "[" + string.Join(", ", d.Items.Select(kv => ToStr(kv.Key, depth + 1, true) + ": " + ToStr(kv.Value, depth + 1, true))) + "]";
			case KTupleObject t:
				return "(" + string.Join(", ", t.Items.Select((x, i) =>
					(t.Names?[i] is { } n ? n + "=" : "") + ToStr(x, depth + 1, true))) + ")";
			case KResultObject r: return (r.IsOk ? "Ok(" : "Err(") + ToStr(r.Value, depth + 1, true) + ")";
			case KTypeObject t: return t.Type.Name;
			case KNativeBox nb: return nb.Value.ToString() ?? "";
			case KTask t: return t.IsCompleted ? (t.IsFaulted ? "<task faulted>" : "<task done>") : "<task pending>";
			default: return o.ToString() ?? "";
		}
	}

	/// <summary>Поля объекта в порядке объявления (предки, затем свои), нестатические.</summary>
	public static IEnumerable<FieldDef> AllFields(KObject k)
	{
		var info = k.Type.Module.Classes.Length > k.Type.DefIndex ? k.Type.Module.Classes[k.Type.DefIndex] : null;
		if (info is null) return k.Type.Def.Fields.Where(f => !f.Flags.HasFlag(MemberFlags.Static));
		return info.AllFields;
	}
}
