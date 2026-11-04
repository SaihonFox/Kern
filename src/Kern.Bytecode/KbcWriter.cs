using System.IO.Compression;
using System.Text;

namespace Kern.Bytecode;

/// <summary>
/// Бинарная запись <see cref="KbcModule"/> (формат KBC2, версия 3, little-endian).
/// <para>
/// Версия 3 уменьшает размер: целые числа и счётчики пишутся LEB128 (код функций,
/// таблицы строк, индексы — самая объёмная часть), канонический текст подписи типа
/// больше не дублируется (читатель строит его из структуры), а весь блок сжимается
/// Brotli, если это выгодно. Порядок секций прежний и зеркален <see cref="KbcReader"/>;
/// запись детерминирована, повторная сериализация прочитанного модуля даёт те же байты.
/// </para>
/// </summary>
public static class KbcWriter
{
	private static readonly UTF8Encoding Utf8 = new(false);

	private const byte FlagCompressed = 1;

	public static byte[] Write(KbcModule module)
	{
		using MemoryStream stream = new();
		Write(module, stream);
		return stream.ToArray();
	}

	public static void Write(KbcModule module, Stream stream)
	{
		ArgumentNullException.ThrowIfNull(module);
		ArgumentNullException.ThrowIfNull(stream);
		KbcModuleValidator.Validate(module);

		byte[] payload;
		using (MemoryStream buffer = new())
		{
			using (BinaryWriter writer = new(buffer, Utf8, leaveOpen: true))
				WritePayload(writer, module);
			payload = buffer.ToArray();
		}

		byte[] compressed;
		using (MemoryStream buffer = new())
		{
			using (BrotliStream brotli = new(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
				brotli.Write(payload);
			compressed = buffer.ToArray();
		}

		bool useCompressed = compressed.Length < payload.Length;
		using BinaryWriter header = new(stream, Utf8, leaveOpen: true);
		header.Write(Encoding.ASCII.GetBytes(KbcModule.Magic));
		header.Write(KbcModule.Version);
		header.Write((byte)(useCompressed ? FlagCompressed : 0));
		header.Write(payload.Length);
		if (useCompressed)
		{
			header.Write(compressed.Length);
			header.Write(compressed);
		}
		else
		{
			header.Write(payload);
		}
		header.Flush();
	}

	private static void WritePayload(BinaryWriter writer, KbcModule module)
	{
		WriteString(writer, module.Name);
		WriteStrings(writer, module.References);
		WriteStrings(writer, module.StringPool);
		WriteList(writer, module.ConstPool, WriteConst);
		WriteList(writer, module.TypeSigPool, WriteSig);
		WriteList(writer, module.TypeSigListPool, WriteIntArray);
		WriteList(writer, module.TypePool, WriteType);
		WriteList(writer, module.FieldPool, WriteField);
		WriteList(writer, module.PropertyPool, WriteProperty);
		WriteList(writer, module.FunctionPool, WriteFunction);
		WriteInt32(writer, module.EntryPoint);
		WriteInt32(writer, module.StaticInit);
		WriteList(writer, module.Attributes, WriteAttribute);
	}

	private static void WriteUInt32(BinaryWriter writer, uint value)
	{
		while (value >= 0x80)
		{
			writer.Write((byte)(value | 0x80));
			value >>= 7;
		}
		writer.Write((byte)value);
	}

	/// <summary>ZigZag + LEB128: отрицательные индексы (-1) тоже занимают один байт.</summary>
	private static void WriteInt32(BinaryWriter writer, int value) =>
		WriteUInt32(writer, (uint)((value << 1) ^ (value >> 31)));

	private static void WriteInt64(BinaryWriter writer, long value) =>
		WriteUInt64(writer, (ulong)((value << 1) ^ (value >> 63)));

	private static void WriteUInt64(BinaryWriter writer, ulong value)
	{
		while (value >= 0x80)
		{
			writer.Write((byte)(value | 0x80));
			value >>= 7;
		}
		writer.Write((byte)value);
	}

	private static void WriteList<T>(BinaryWriter writer, IReadOnlyList<T> items, Action<BinaryWriter, T> write)
	{
		WriteUInt32(writer, (uint)items.Count);
		foreach (T item in items) write(writer, item);
	}

	private static void WriteStrings(BinaryWriter writer, IReadOnlyList<string> items)
	{
		WriteUInt32(writer, (uint)items.Count);
		foreach (string item in items) WriteString(writer, item);
	}

	private static void WriteString(BinaryWriter writer, string value)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(value);
		WriteUInt32(writer, (uint)bytes.Length);
		writer.Write(bytes);
	}

	private static void WriteNullableString(BinaryWriter writer, string? value)
	{
		writer.Write(value is not null);
		if (value is not null) WriteString(writer, value);
	}

	private static void WriteIntArray(BinaryWriter writer, int[] items)
	{
		WriteUInt32(writer, (uint)items.Length);
		foreach (int item in items) WriteInt32(writer, item);
	}

	private static void WriteConst(BinaryWriter writer, KConst value)
	{
		writer.Write((byte)value.Kind);
		switch (value.Kind)
		{
			case KConstKind.Nil or KConstKind.None or KConstKind.Void:
				break;
			case KConstKind.Bool:
				writer.Write(value.AsBool);
				break;
			case KConstKind.I64:
				WriteInt64(writer, value.Bits);
				break;
			case KConstKind.F64:
				writer.Write(value.Bits);
				break;
			case KConstKind.Char:
				WriteUInt32(writer, (ushort)value.AsChar);
				break;
			case KConstKind.Str:
				WriteString(writer, value.AsStr);
				break;
			default:
				throw new KbcFormatException($"константа неизвестного вида: {value.Kind}");
		}
	}

	private static void WriteSig(BinaryWriter writer, TypeSig sig)
	{
		// Канонический текст не пишется: он полностью выводится из структуры.
		switch (sig)
		{
			case TypeSig.PrimSig prim:
				writer.Write((byte)1);
				writer.Write((byte)prim.Kind);
				break;
			case TypeSig.NamedSig named:
				writer.Write((byte)2);
				WriteString(writer, named.FullName);
				WriteList(writer, named.Args, WriteSig);
				break;
			case TypeSig.ParamSig param:
				writer.Write((byte)3);
				WriteInt32(writer, param.Scope);
				WriteInt32(writer, param.Index);
				WriteString(writer, param.Name);
				break;
			case TypeSig.FuncSig func:
				writer.Write((byte)4);
				WriteList(writer, func.Params, WriteSig);
				WriteSig(writer, func.Return);
				break;
			case TypeSig.TupleSig tuple:
				writer.Write((byte)5);
				WriteList(writer, tuple.Items, WriteSig);
				writer.Write(tuple.Names is not null);
				if (tuple.Names is not null)
					foreach (string? name in tuple.Names) WriteNullableString(writer, name);
				break;
			case TypeSig.NullableSig nullable:
				writer.Write((byte)6);
				WriteSig(writer, nullable.Inner);
				break;
			case TypeSig.UnionSig union:
				writer.Write((byte)7);
				WriteList(writer, union.Members, WriteSig);
				break;
			default:
				throw new KbcFormatException($"подпись типа неизвестного вида: {sig.GetType().Name}");
		}
	}

	private static void WriteAttribute(BinaryWriter writer, AttributeUse attribute)
	{
		WriteString(writer, attribute.Name);
		WriteList(writer, attribute.Args, WriteConst);
	}

	private static void WriteField(BinaryWriter writer, FieldDef field)
	{
		WriteString(writer, field.Name);
		WriteSig(writer, field.Type);
		WriteUInt32(writer, (uint)field.Flags);
		WriteInt32(writer, field.Slot);
		WriteList(writer, field.Attributes, WriteAttribute);
	}

	private static void WriteProperty(BinaryWriter writer, PropertyDef property)
	{
		WriteString(writer, property.Name);
		WriteSig(writer, property.Type);
		WriteUInt32(writer, (uint)property.Flags);
		WriteInt32(writer, property.OwnerType);
		WriteInt32(writer, property.Getter);
		WriteInt32(writer, property.Setter);
		WriteInt32(writer, property.Init);
		WriteInt32(writer, property.BackingField);
		WriteList(writer, property.Attributes, WriteAttribute);
	}

	private static void WriteFunction(BinaryWriter writer, FunctionDef function)
	{
		WriteString(writer, function.Name);
		WriteInt32(writer, function.OwnerType);
		WriteUInt32(writer, (uint)function.Flags);
		WriteList(writer, function.ParamTypes, WriteSig);
		WriteSig(writer, function.ReturnType);
		WriteInt32(writer, function.LocalCount);
		WriteInt32(writer, function.MaxStack);
		WriteIntArray(writer, function.Code);
		WriteList(writer, function.LineTable, (w, line) =>
		{
			WriteInt32(w, line.Offset);
			WriteInt32(w, line.Line);
		});
		WriteNullableString(writer, function.SourceFile);
		WriteStrings(writer, function.LocalNames);
		WriteNullableString(writer, function.NativeKey);
		WriteInt32(writer, function.NativeTypeArgs);
		WriteList(writer, function.Attributes, WriteAttribute);
	}

	private static void WriteType(BinaryWriter writer, TypeDef type)
	{
		WriteString(writer, type.Name);
		WriteString(writer, type.FullName);
		writer.Write((byte)type.Kind);
		WriteUInt32(writer, (uint)type.Flags);
		WriteStrings(writer, type.GenericParams);
		WriteList(writer, type.Parents, WriteSig);
		WriteList(writer, type.Fields, WriteField);
		WriteList(writer, type.Properties, WriteProperty);
		WriteList(writer, type.Methods, WriteInt32);
		WriteList(writer, type.Ctors, WriteInt32);
		WriteList(writer, type.Attributes, WriteAttribute);
		WriteList(writer, type.UnionMembers, WriteSig);
	}
}
