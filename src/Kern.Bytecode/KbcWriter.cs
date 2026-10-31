using System.Text;

namespace Kern.Bytecode;

/// <summary>
/// Бинарная запись <see cref="KbcModule"/> (формат KBC2, версия 2, little-endian).
/// Порядок секций фиксирован и зеркален <see cref="KbcReader"/>; запись детерминирована,
/// поэтому повторная сериализация прочитанного модуля даёт те же байты.
/// </summary>
public static class KbcWriter
{
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

		using BinaryWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true);
		writer.Write(Encoding.ASCII.GetBytes(KbcModule.Magic));
		writer.Write(KbcModule.Version);
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
		writer.Write(module.EntryPoint);
		writer.Write(module.StaticInit);
		WriteList(writer, module.Attributes, WriteAttribute);
		writer.Flush();
	}

	private static void WriteList<T>(BinaryWriter writer, IReadOnlyList<T> items, Action<BinaryWriter, T> write)
	{
		writer.Write(items.Count);
		foreach (T item in items) write(writer, item);
	}

	private static void WriteStrings(BinaryWriter writer, IReadOnlyList<string> items)
	{
		writer.Write(items.Count);
		foreach (string item in items) WriteString(writer, item);
	}

	private static void WriteString(BinaryWriter writer, string value)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(value);
		writer.Write(bytes.Length);
		writer.Write(bytes);
	}

	private static void WriteNullableString(BinaryWriter writer, string? value)
	{
		writer.Write(value is not null);
		if (value is not null) WriteString(writer, value);
	}

	private static void WriteIntArray(BinaryWriter writer, int[] items)
	{
		writer.Write(items.Length);
		foreach (int item in items) writer.Write(item);
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
			case KConstKind.I64 or KConstKind.F64:
				writer.Write(value.Bits);
				break;
			case KConstKind.Char:
				writer.Write((ushort)value.AsChar);
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
		// Канонический текст пишется рядом со структурой: читатель сверяет их.
		WriteString(writer, sig.Canonical);
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
				writer.Write(param.Scope);
				writer.Write(param.Index);
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
		writer.Write((uint)field.Flags);
		writer.Write(field.Slot);
		WriteList(writer, field.Attributes, WriteAttribute);
	}

	private static void WriteProperty(BinaryWriter writer, PropertyDef property)
	{
		WriteString(writer, property.Name);
		WriteSig(writer, property.Type);
		writer.Write((uint)property.Flags);
		writer.Write(property.Getter);
		writer.Write(property.Setter);
		writer.Write(property.Init);
		writer.Write(property.BackingField);
		WriteList(writer, property.Attributes, WriteAttribute);
	}

	private static void WriteFunction(BinaryWriter writer, FunctionDef function)
	{
		WriteString(writer, function.Name);
		writer.Write(function.OwnerType);
		writer.Write((uint)function.Flags);
		WriteList(writer, function.ParamTypes, WriteSig);
		WriteSig(writer, function.ReturnType);
		writer.Write(function.LocalCount);
		writer.Write(function.MaxStack);
		WriteIntArray(writer, function.Code);
		writer.Write(function.LineTable.Count);
		foreach (LineEntry line in function.LineTable)
		{
			writer.Write(line.Offset);
			writer.Write(line.Line);
		}
		WriteNullableString(writer, function.SourceFile);
		WriteNullableString(writer, function.NativeKey);
		writer.Write(function.NativeTypeArgs);
		WriteList(writer, function.Attributes, WriteAttribute);
	}

	private static void WriteType(BinaryWriter writer, TypeDef type)
	{
		WriteString(writer, type.Name);
		WriteString(writer, type.FullName);
		writer.Write((byte)type.Kind);
		writer.Write((uint)type.Flags);
		WriteStrings(writer, type.GenericParams);
		WriteList(writer, type.Parents, WriteSig);
		WriteList(writer, type.Fields, WriteField);
		WriteList(writer, type.Properties, WriteProperty);
		WriteList(writer, type.Methods, (w, value) => w.Write(value));
		WriteList(writer, type.Ctors, (w, value) => w.Write(value));
		WriteList(writer, type.Attributes, WriteAttribute);
		WriteList(writer, type.UnionMembers, WriteSig);
	}
}
