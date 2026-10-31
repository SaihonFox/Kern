using System.Globalization;
using System.Text;
using System.Text.Json;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Bytecode;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Json;

/// <summary>
/// JSON: <c>json.parse(text)</c> → динамические значения (dict&lt;str, object&gt;, list&lt;object&gt;, числа, str, bool, nil);
/// <c>json.to_str(value)</c> — сериализация любого значения (в т.ч. unit/классов по полям);
/// <c>json.from_str&lt;T&gt;(text)</c> — типизированная десериализация.
/// </summary>
[KernType("json")]
public static class JsonApi
{
	// ---------- чтение ----------

	[KernStatic] public static KResult<object, string> Parse(string text)
	{
		try
		{
			using var doc = JsonDocument.Parse(text);
			return KResult<object, string>.Ok(ToDynamic(doc.RootElement));
		}
		catch (JsonException e) { return Kv.Err<object>(e.Message); }
	}

	private static KValue ToDynamic(JsonElement e)
	{
		switch (e.ValueKind)
		{
			case JsonValueKind.Object:
			{
				var d = new KDictObject(TypeFactory.Str, TypeFactory.Object);
				foreach (var p in e.EnumerateObject()) d.Items[KValue.Str(p.Name)] = ToDynamic(p.Value);
				return KValue.Obj(d);
			}
			case JsonValueKind.Array:
				return KValue.Obj(new KListObject(TypeFactory.Object, [.. e.EnumerateArray().Select(ToDynamic)]));
			case JsonValueKind.String: return KValue.Str(e.GetString()!);
			case JsonValueKind.Number: return e.TryGetInt64(out long l) ? KValue.I64(l) : KValue.F64(e.GetDouble());
			case JsonValueKind.True: return KValue.True;
			case JsonValueKind.False: return KValue.False;
			default: return KValue.Nil;
		}
	}

	/// <summary>Десериализация в тип T (поля unit/классов заполняются напрямую, конструкторы не вызываются).</summary>
	[KernStatic] public static KResult<T, string> FromStr<T>(string text, [KernVm] Interpreter vm, [KernTypeArgs] KType[] typeArgs)
	{
		try
		{
			using var doc = JsonDocument.Parse(text);
			return KResult<T, string>.Ok(FromElement(vm, doc.RootElement, Kv.Last(typeArgs)));
		}
		catch (Exception e) when (e is JsonException or KernPanic or InvalidOperationException or FormatException or OverflowException)
		{
			return Kv.Err<T>(e.Message);
		}
	}

	/// <summary>JSON-текст → значение заданного типа (для ORM: вложенные поля хранятся как JSON).</summary>
	public static KValue FromJsonText(Interpreter vm, string text, KType type)
	{
		using var doc = JsonDocument.Parse(text);
		return FromElement(vm, doc.RootElement, type);
	}

	private static KValue FromElement(Interpreter vm, JsonElement e, KType type)
	{
		switch (type)
		{
			case KNullableType n:
				return e.ValueKind == JsonValueKind.Null ? KValue.Nil : FromElement(vm, e, n.Inner);
			case KPrimType p:
				return FromPrimitive(e, p.Kind);
			case KBuiltinGenericType { Builtin: "list" } l:
				return KValue.Obj(new KListObject(l.Args[0], [.. Items(e).Select(x => FromElement(vm, x, l.Args[0]))]));
			case KBuiltinGenericType { Builtin: "array" } a:
				return KValue.Obj(new KArrayObject(a.Args[0], [.. Items(e).Select(x => FromElement(vm, x, a.Args[0]))]));
			case KBuiltinGenericType { Builtin: "dict" } d:
			{
				if (e.ValueKind != JsonValueKind.Object) throw new KernPanic("ожидался JSON-объект");
				var res = new KDictObject(d.Args[0], d.Args[1]);
				foreach (var p in e.EnumerateObject()) res.Items[KValue.Str(p.Name)] = FromElement(vm, p.Value, d.Args[1]);
				return KValue.Obj(res);
			}
			case KClassType c:
				return FromObject(vm, e, c);
			default:
				return ToDynamic(e);
		}
	}

	private static IEnumerable<JsonElement> Items(JsonElement e) =>
		e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : throw new KernPanic("ожидался JSON-массив");

	private static KValue FromObject(Interpreter vm, JsonElement e, KClassType c)
	{
		if (c.Def.Kind == TypeKind.Enum)
		{
			string name = e.GetString() ?? "";
			int i = c.Def.EnumNames.IndexOf(name);
			if (i < 0) throw new KernPanic($"'{name}' не является значением {c.Def.Name}");
			return vm.GetEnumValue(c.DefIndex, i);
		}
		if (e.ValueKind != JsonValueKind.Object) throw new KernPanic($"для {c.Def.Name} ожидался JSON-объект");
		var info = vm.Info.Classes[c.DefIndex];
		var fields = new KValue[info.FieldCount];
		foreach (var f in info.AllFields)
		{
			if (!e.TryGetProperty(f.Name, out var v) && !e.TryGetProperty(SnakeToCamel(f.Name), out v)) continue;
			fields[f.Slot] = FromElement(vm, v, vm.Types.Resolve(f.Type, c.Args, []));
		}
		return KValue.Obj(new KObject(c, fields));
	}

	private static string SnakeToCamel(string s)
	{
		var sb = new StringBuilder();
		bool up = false;
		foreach (char ch in s)
		{
			if (ch == '_') { up = true; continue; }
			sb.Append(up ? char.ToUpperInvariant(ch) : ch);
			up = false;
		}
		return sb.ToString();
	}

	private static KValue FromPrimitive(JsonElement e, PrimKind kind)
	{
		switch (kind)
		{
			case PrimKind.Str: return KValue.Str(e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText());
			case PrimKind.Bool: return KValue.Bool(e.GetBoolean());
			case PrimKind.Char: return KValue.Char(e.GetString()![0]);
			case PrimKind.F16 or PrimKind.F32 or PrimKind.F64:
				return Num.FromDouble(kind switch { PrimKind.F16 => ValueKind.F16, PrimKind.F32 => ValueKind.F32, _ => ValueKind.F64 }, e.GetDouble());
			case PrimKind.Object: return ToDynamic(e);
			default:
			{
				var vk = kind switch
				{
					PrimKind.I8 => ValueKind.I8, PrimKind.I16 => ValueKind.I16, PrimKind.I32 => ValueKind.I32, PrimKind.I64 => ValueKind.I64,
					PrimKind.U8 => ValueKind.U8, PrimKind.U16 => ValueKind.U16, PrimKind.U32 => ValueKind.U32, PrimKind.U64 => ValueKind.U64,
					_ => throw new KernPanic($"JSON: тип {kind} не поддерживается"),
				};
				return Num.Convert(KValue.I64(e.GetInt64()), vk);
			}
		}
	}

	// ---------- запись ----------

	[KernStatic] public static string ToStr([KernVm] Interpreter vm, KValue value, bool pretty = false)
	{
		var sb = new StringBuilder();
		Write(vm, value, sb, pretty, 0);
		return sb.ToString();
	}

	private static void Newline(StringBuilder sb, bool pretty, int depth)
	{
		if (!pretty) return;
		sb.Append('\n').Append(' ', depth * 2);
	}

	private static void Write(Interpreter vm, KValue v, StringBuilder sb, bool pretty, int depth)
	{
		if (v.IsNilOrNone || v.Kind == ValueKind.Void) { sb.Append("null"); return; }
		switch (v.Kind)
		{
			case ValueKind.Bool: sb.Append(v.Bits != 0 ? "true" : "false"); return;
			case ValueKind.Str: Quote(sb, v.AsStr); return;
			case ValueKind.Char: Quote(sb, ((char)v.Bits).ToString()); return;
		}
		if (v.IsFloat) { sb.Append(v.AsF64.ToString("R", CultureInfo.InvariantCulture)); return; }
		if (v.IsInteger) { sb.Append(v.Kind == ValueKind.U64 ? ((ulong)v.Bits).ToString(CultureInfo.InvariantCulture) : v.Bits.ToString(CultureInfo.InvariantCulture)); return; }

		switch (v.Ref)
		{
			case KListObject l: WriteArray(vm, l.Items, sb, pretty, depth); return;
			case KArrayObject a: WriteArray(vm, a.Items, sb, pretty, depth); return;
			case KTupleObject t: WriteArray(vm, t.Items, sb, pretty, depth); return;
			case KDictObject d:
				WriteObject(vm, d.Items.Select(p => (ValueOps.ToStr(p.Key), p.Value)), sb, pretty, depth);
				return;
			case KObject o:
			{
				var info = vm.Info.Classes[o.Type.DefIndex];
				if (o.Type.Def.Kind == TypeKind.Enum) { Quote(sb, vm.Stringify(v)); return; }
				WriteObject(vm, info.AllFields.Select(f => (f.Name, o.Fields[f.Slot])), sb, pretty, depth);
				return;
			}
			case KNativeBox box: Quote(sb, Convert.ToString(box.Value, CultureInfo.InvariantCulture) ?? ""); return;
			default: Quote(sb, vm.Stringify(v)); return;
		}
	}

	private static void WriteArray(Interpreter vm, IEnumerable<KValue> items, StringBuilder sb, bool pretty, int depth)
	{
		var list = items.ToList();
		if (list.Count == 0) { sb.Append("[]"); return; }
		sb.Append('[');
		for (int i = 0; i < list.Count; i++)
		{
			if (i > 0) sb.Append(',');
			Newline(sb, pretty, depth + 1);
			Write(vm, list[i], sb, pretty, depth + 1);
		}
		Newline(sb, pretty, depth);
		sb.Append(']');
	}

	private static void WriteObject(Interpreter vm, IEnumerable<(string Name, KValue Value)> props, StringBuilder sb, bool pretty, int depth)
	{
		var list = props.ToList();
		if (list.Count == 0) { sb.Append("{}"); return; }
		sb.Append('{');
		for (int i = 0; i < list.Count; i++)
		{
			if (i > 0) sb.Append(',');
			Newline(sb, pretty, depth + 1);
			Quote(sb, list[i].Name);
			sb.Append(pretty ? ": " : ":");
			Write(vm, list[i].Value, sb, pretty, depth + 1);
		}
		Newline(sb, pretty, depth);
		sb.Append('}');
	}

	private static void Quote(StringBuilder sb, string s)
	{
		sb.Append('"');
		foreach (char c in s)
		{
			switch (c)
			{
				case '"': sb.Append("\\\""); break;
				case '\\': sb.Append("\\\\"); break;
				case '\n': sb.Append("\\n"); break;
				case '\r': sb.Append("\\r"); break;
				case '\t': sb.Append("\\t"); break;
				default:
					if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
					else sb.Append(c);
					break;
			}
		}
		sb.Append('"');
	}
}
