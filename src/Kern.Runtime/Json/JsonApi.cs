using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kern.Binding.Attributes;
using Kern.Binding.Formatting;
using Kern.Binding.Marshalling;
using Kern.Binding.Values;
using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Json;

/// <summary>
/// <c>kern.json</c> (spec §16, раунд 4): сериализация значений Kern в JSON и разбор обратно.
/// Имена полей берутся из <c>TypeDef.Fields</c>; <c>from_str&lt;T&gt;</c> получает reified-тип
/// через <c>[KernTypeArgs]</c> и строит объект нужного Kern-типа. Ошибки разбора — <c>Err</c>
/// с русским текстом, а не паника.
/// </summary>
[KernModule("kern.json")]
public static class JsonApi
{
	private static readonly JsonSerializerOptions Compact = new()
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	private static readonly JsonSerializerOptions Indented = new()
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		WriteIndented = true,
	};

	/// <summary><c>json.to_str(value)</c>: компактный JSON (кириллица без \u-экранирования).</summary>
	[KernMember]
	public static string ToStr(KValue value) =>
		(ToNode(value) ?? JsonValue.Create((string?)null))!.ToJsonString(Compact);

	/// <summary><c>json.to_str_pretty(value)</c>: JSON с отступами.</summary>
	[KernMember]
	public static string ToStrPretty(KValue value) =>
		(ToNode(value) ?? JsonValue.Create((string?)null))!.ToJsonString(Indented);

	/// <summary>
	/// <c>json.parse(text)</c>: разобрать JSON в значение Kern. Объекты становятся
	/// <c>dict&lt;str, object&gt;</c>, массивы — <c>list&lt;object&gt;</c>, числа — <c>i64</c>/<c>f64</c>.
	/// </summary>
	[KernMember]
	public static KResult<KValue, string> Parse(string text, Interpreter interpreter)
	{
		try
		{
			JsonNode? node = JsonNode.Parse(text);
			return KResult<KValue, string>.Success(
				FromNode(node, interpreter.Types.Prim(PrimKind.Object), interpreter.Types));
		}
		catch (JsonException exception)
		{
			return KResult<KValue, string>.Failure($"json: {exception.Message}");
		}
	}

	/// <summary>
	/// <c>json.from_str&lt;T&gt;(text)</c>: разобрать JSON в значение типа <c>T</c>.
	/// Тип приходит reified-аргументом (generic-параметр C# не используется телом).
	/// </summary>
	[KernMember]
	[KernReturn("result<T, str>")]
	public static KValue FromStr<T>(
		string text,
		Interpreter interpreter,
		[KernTypeArgs] KType[] typeArguments)
	{
		if (typeArguments.Length != 1)
			return Result(interpreter, ok: false, KValue.Str("json.from_str: нужен один аргумент типа"), typeArguments);

		try
		{
			JsonNode? node = JsonNode.Parse(text);
			KValue value = FromNode(node, typeArguments[0], interpreter.Types);
			return Result(interpreter, ok: true, value, typeArguments);
		}
		catch (JsonException exception)
		{
			return Result(interpreter, ok: false, KValue.Str($"json: {exception.Message}"), typeArguments);
		}
		catch (InvalidOperationException exception)
		{
			return Result(interpreter, ok: false, KValue.Str($"json: {exception.Message}"), typeArguments);
		}
	}

	/// <summary>
	/// Собрать <c>Ok&lt;T&gt;</c>/<c>Err&lt;str&gt;</c> с reified-аргументом вызывающего кода:
	/// CLR-закрытие generic-метода даёт <c>object</c>, а union в Kern должен видеть настоящий T.
	/// </summary>
	private static KValue Result(
		Interpreter interpreter,
		bool ok,
		KValue payload,
		IReadOnlyList<KType> typeArguments)
	{
		string variant = ok ? "Ok" : "Err";
		TypeDef? definition = null;
		foreach (TypeDef type in interpreter.Module.TypePool)
		{
			if (type.Name != variant || type.GenericParams.Count != 1 || type.Fields.Count != 1)
				continue;
			definition = type;
			break;
		}

		if (definition is null)
			throw new KernPanic($"json: тип '{variant}' не найден в модуле '{interpreter.Module.Name}'");

		KType argument = ok
			? typeArguments[0]
			: interpreter.Types.Prim(PrimKind.Str);
		KClassType resultType = interpreter.Types.Named(definition, argument);
		var fields = new KValue[definition.Fields.Count];
		Array.Fill(fields, KValue.None);
		fields[0] = payload;
		return KValue.Obj(new KObject(resultType, fields));
	}

	private static JsonNode? ToNode(KValue value)
	{
		switch (value.Kind)
		{
			case ValueKind.None:
			case ValueKind.Nil:
				return null;
			case ValueKind.Bool:
				return JsonValue.Create(value.AsBool);
			case ValueKind.Char:
				return JsonValue.Create(value.AsChar.ToString());
			case ValueKind.Str:
				return JsonValue.Create(value.AsStr);
			case ValueKind.I8:
			case ValueKind.I16:
			case ValueKind.I32:
			case ValueKind.I64:
				return JsonValue.Create(value.AsI64);
			case ValueKind.U8:
			case ValueKind.U16:
			case ValueKind.U32:
			case ValueKind.U64:
				return JsonValue.Create(value.AsU64);
			case ValueKind.F16:
			case ValueKind.F32:
			case ValueKind.F64:
				return JsonValue.Create(value.AsF64);
			case ValueKind.Ref:
				return ReferenceToNode(value.Ref);
			default:
				throw new KernPanic($"json: значение типа {value.Kind} нельзя сериализовать");
		}
	}

	private static JsonNode? ReferenceToNode(object reference) => reference switch
	{
		KList list => ListToNode(list.Items),
		KArray array => ListToNode(array.Items.ToArray()),
		KDict dict => DictionaryToNode(dict),
		KTuple tuple => ListToNode(tuple.Items.ToArray()),
		KObject value => ObjectToNode(value),
		_ => throw new KernPanic($"json: значение типа {reference.GetType().Name} нельзя сериализовать"),
	};

	private static JsonArray ListToNode(IEnumerable<KValue> items)
	{
		var array = new JsonArray();
		foreach (KValue item in items)
			array.Add(ToNode(item));
		return array;
	}

	private static JsonObject DictionaryToNode(KDict dict)
	{
		var result = new JsonObject();
		foreach (var (key, value) in dict.Items)
			result[KeyText(key)] = ToNode(value);
		return result;
	}

	private static JsonObject ObjectToNode(KObject value)
	{
		var result = new JsonObject();
		foreach (FieldDef field in value.Type.Definition.Fields)
		{
			if ((field.Flags & MemberFlags.Static) != 0 || IsPadding(field.Name))
				continue;
			result[field.Name] = ToNode(value.GetField(field.Slot));
		}

		return result;
	}

	private static string KeyText(KValue key) => key.Kind switch
	{
		ValueKind.Str => key.AsStr,
		ValueKind.Char => key.AsChar.ToString(),
		_ => KernFormat.Value(key),
	};

	private static KValue FromNode(JsonNode? node, KType type, TypeFactory factory)
	{
		if (type is KNullableType nullable)
			return node is null ? KValue.Nil : FromNode(node, nullable.Inner, factory);
		if (node is null)
			return KValue.Nil;

		switch (type)
		{
			case KPrimType primitive:
				return FromPrimitive(node, primitive.Prim, factory);
			case KBuiltinGenericType { Builtin: BuiltinGenericKind.List } list:
				return KValue.Obj(FromList(node, list.TypeArgs[0], factory));
			case KBuiltinGenericType { Builtin: BuiltinGenericKind.Array } array:
				return KValue.Obj(FromArray(node, array.TypeArgs[0], factory));
			case KBuiltinGenericType { Builtin: BuiltinGenericKind.Dict } dict:
				return KValue.Obj(FromDictionary(node, dict.TypeArgs[0], dict.TypeArgs[1], factory));
			case KClassType @class:
				return FromObject(node, @class, factory);
			case KTupleType tuple:
				return FromTuple(node, tuple, factory);
			case KUnionType union:
				return FromUnion(node, union, factory);
			default:
				throw new InvalidOperationException($"тип '{type.Canonical}' не поддержан json.from_str");
		}
	}

	private static KValue FromPrimitive(JsonNode node, PrimKind kind, TypeFactory factory) => kind switch
	{
		PrimKind.Bool => KValue.Bool(node.GetValue<bool>()),
		PrimKind.Str => KValue.Str(node.GetValue<string>()),
		PrimKind.Char => KValue.Char(CharOf(node)),
		PrimKind.I8 => KValue.I8((sbyte)node.GetValue<long>()),
		PrimKind.I16 => KValue.I16((short)node.GetValue<long>()),
		PrimKind.I32 => KValue.I32((int)node.GetValue<long>()),
		PrimKind.I64 => KValue.I64(node.GetValue<long>()),
		PrimKind.U8 => KValue.U8((byte)node.GetValue<ulong>()),
		PrimKind.U16 => KValue.U16((ushort)node.GetValue<ulong>()),
		PrimKind.U32 => KValue.U32((uint)node.GetValue<ulong>()),
		PrimKind.U64 => KValue.U64(node.GetValue<ulong>()),
		PrimKind.F16 => KValue.F16((Half)node.GetValue<double>()),
		PrimKind.F32 => KValue.F32((float)node.GetValue<double>()),
		PrimKind.F64 => KValue.F64(node.GetValue<double>()),
		PrimKind.Object => FromDynamic(node, factory),
		_ => throw new InvalidOperationException($"тип '{kind}' не поддержан json.from_str"),
	};

	private static KValue FromDynamic(JsonNode node, TypeFactory factory) => node switch
	{
		JsonObject => KValue.Obj(FromDictionary(
			node,
			factory.Prim(PrimKind.Str),
			factory.Prim(PrimKind.Object),
			factory)),
		JsonArray => KValue.Obj(FromList(node, factory.Prim(PrimKind.Object), factory)),
		_ => FromPrimitive(node, InferPrimitive(node), factory),
	};

	private static PrimKind InferPrimitive(JsonNode node) =>
		node.GetValueKind() switch
		{
			JsonValueKind.True or JsonValueKind.False => PrimKind.Bool,
			JsonValueKind.String => PrimKind.Str,
			JsonValueKind.Number => node.AsValue().TryGetValue<long>(out _) ? PrimKind.I64 : PrimKind.F64,
			_ => PrimKind.Object,
		};

	private static char CharOf(JsonNode node)
	{
		string text = node.GetValue<string>();
		if (text.Length != 1)
			throw new InvalidOperationException("json: ожидался один символ");
		return text[0];
	}

	private static KList FromList(JsonNode node, KType element, TypeFactory factory)
	{
		var list = new KList(element);
		foreach (JsonNode? item in node.AsArray())
			list.Add(FromNode(item, element, factory));
		return list;
	}

	private static KArray FromArray(JsonNode node, KType element, TypeFactory factory)
	{
		JsonArray array = node.AsArray();
		var items = new KValue[array.Count];
		for (var index = 0; index < array.Count; index++)
			items[index] = FromNode(array[index], element, factory);
		return new KArray(element, items);
	}

	private static KDict FromDictionary(JsonNode node, KType key, KType value, TypeFactory factory)
	{
		var dict = new KDict(key, value);
		foreach (var (name, item) in node.AsObject())
			dict.Set(FromNode(JsonValue.Create(name), key, factory), FromNode(item, value, factory));
		return dict;
	}

	private static KValue FromObject(JsonNode node, KClassType type, TypeFactory factory)
	{
		JsonObject source = node.AsObject();
		IReadOnlyList<FieldDef> fields = type.Definition.Fields;
		var values = new KValue[fields.Count];
		for (var slot = 0; slot < fields.Count; slot++)
		{
			FieldDef field = fields[slot];
			if ((field.Flags & MemberFlags.Static) != 0 || IsPadding(field.Name))
			{
				values[slot] = KValue.None;
				continue;
			}

			KType fieldType = factory.FromSig(field.Type, type);
			values[slot] = source.TryGetPropertyValue(field.Name, out JsonNode? item)
				? FromNode(item, fieldType, factory)
				: KValue.Nil;
		}

		return KValue.Obj(new KObject(type, values, isUnit: type.Definition.Kind is TypeDefKind.Unit));
	}

	private static KValue FromTuple(JsonNode node, KTupleType tuple, TypeFactory factory)
	{
		JsonArray source = node.AsArray();
		var items = new KValue[tuple.Items.Length];
		for (var index = 0; index < items.Length; index++)
			items[index] = index < source.Count
				? FromNode(source[index], tuple.Items[index], factory)
				: KValue.Nil;
		return KValue.Obj(new KTuple(tuple.Items, items));
	}

	private static KValue FromUnion(JsonNode node, KUnionType union, TypeFactory factory)
	{
		foreach (KType member in union.Members)
		{
			try
			{
				return FromNode(node, member, factory);
			}
			catch (Exception exception) when (exception is InvalidOperationException or FormatException)
			{
				// Проба следующего варианта: union — это набор допустимых форм JSON.
			}
		}

		throw new InvalidOperationException($"json: ни один вариант '{union.Canonical}' не подошёл");
	}

	private static bool IsPadding(string name) => name.StartsWith("$slot", StringComparison.Ordinal);
}
