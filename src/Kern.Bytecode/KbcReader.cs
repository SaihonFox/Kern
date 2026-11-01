using System.Text;

namespace Kern.Bytecode;

/// <summary>
/// Бинарное чтение <see cref="KbcModule"/> (формат KBC2, версия 2, little-endian).
/// Любое повреждение файла даёт <see cref="KbcFormatException"/> с русским сообщением,
/// а не выход за границы массива или падение внутри VM.
/// </summary>
public static class KbcReader
{
	private const int MaxItems = 1_000_000;
	private const int MaxStringBytes = 64 * 1024 * 1024;
	private static readonly UTF8Encoding StrictUtf8 = new(false, true);

	public static KbcModule Read(byte[] data)
	{
		ArgumentNullException.ThrowIfNull(data);
		using MemoryStream stream = new(data, writable: false);
		return Read(stream);
	}

	public static KbcModule Read(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream);
		try
		{
			return ReadCore(stream);
		}
		catch (KbcFormatException)
		{
			throw;
		}
		catch (EndOfStreamException ex)
		{
			throw new KbcFormatException("неожиданный конец файла .kbc: данные обрезаны", ex);
		}
		catch (IOException ex)
		{
			throw new KbcFormatException("не удалось прочитать .kbc", ex);
		}
	}

	private static KbcModule ReadCore(Stream stream)
	{
		using BinaryReader reader = new(stream, StrictUtf8, leaveOpen: true);

		byte[] magic = reader.ReadBytes(4);
		if (magic.Length != 4 || !magic.AsSpan().SequenceEqual("KBC2"u8))
			throw new KbcFormatException("это не модуль Kern: неверная сигнатура (ожидалось KBC2)");

		ushort version = reader.ReadUInt16();
		if (version != KbcModule.Version)
			throw new KbcFormatException(
				$"неподдерживаемая версия .kbc: {version}; поддерживается {KbcModule.Version}");

		KbcModule module = new() { Name = ReadString(reader) };
		module.References = ReadStrings(reader);
		module.StringPool = ReadStrings(reader);
		module.ConstPool = ReadList(reader, ReadConst, "констант");
		module.TypeSigPool = ReadList(reader, ReadSig, "подписей типов");
		module.TypeSigListPool = ReadList(reader, ReadIntArray, "списков подписей");
		module.TypePool = ReadList(reader, ReadType, "типов");
		module.FieldPool = ReadList(reader, ReadField, "полей");
		module.PropertyPool = ReadList(reader, ReadProperty, "свойств");
		module.FunctionPool = ReadList(reader, ReadFunction, "функций");
		module.EntryPoint = reader.ReadInt32();
		module.StaticInit = reader.ReadInt32();
		module.Attributes = ReadList(reader, ReadAttribute, "атрибутов");

		KbcModuleValidator.Validate(module);
		return module;
	}

	private static int ReadCount(BinaryReader reader, string what)
	{
		int count = reader.ReadInt32();
		if (count < 0 || count > MaxItems)
			throw new KbcFormatException($"повреждённый .kbc: недопустимое количество {what}: {count}");
		return count;
	}

	private static List<T> ReadList<T>(BinaryReader reader, Func<BinaryReader, T> read, string what)
	{
		int count = ReadCount(reader, what);
		List<T> items = new(count);
		for (int i = 0; i < count; i++) items.Add(read(reader));
		return items;
	}

	private static List<string> ReadStrings(BinaryReader reader)
	{
		int count = ReadCount(reader, "строк");
		List<string> items = new(count);
		for (int i = 0; i < count; i++) items.Add(ReadString(reader));
		return items;
	}

	private static string ReadString(BinaryReader reader)
	{
		int length = reader.ReadInt32();
		if (length < 0 || length > MaxStringBytes)
			throw new KbcFormatException($"повреждённый .kbc: недопустимая длина строки {length}");
		byte[] bytes = reader.ReadBytes(length);
		if (bytes.Length != length)
			throw new KbcFormatException("неожиданный конец файла .kbc: строка обрезана");
		try
		{
			return StrictUtf8.GetString(bytes);
		}
		catch (DecoderFallbackException ex)
		{
			throw new KbcFormatException("повреждённый .kbc: строка не в кодировке UTF-8", ex);
		}
	}

	private static string? ReadNullableString(BinaryReader reader)
	{
		return reader.ReadBoolean() ? ReadString(reader) : null;
	}

	private static int[] ReadIntArray(BinaryReader reader)
	{
		int count = ReadCount(reader, "элементов массива");
		int[] items = new int[count];
		for (int i = 0; i < count; i++) items[i] = reader.ReadInt32();
		return items;
	}

	private static bool ReadBool(BinaryReader reader)
	{
		byte value = reader.ReadByte();
		if (value > 1)
			throw new KbcFormatException($"повреждённый .kbc: недопустимое значение bool {value}");
		return value != 0;
	}

	private static KConst ReadConst(BinaryReader reader)
	{
		byte raw = reader.ReadByte();
		if (raw > (byte)KConstKind.Str)
			throw new KbcFormatException($"повреждённый .kbc: неизвестный вид константы {raw}");
		return (KConstKind)raw switch
		{
			KConstKind.Nil => KConst.Nil,
			KConstKind.None => KConst.None,
			KConstKind.Void => KConst.Void,
			KConstKind.Bool => KConst.Bool(ReadBool(reader)),
			KConstKind.I64 => KConst.I64(reader.ReadInt64()),
			KConstKind.F64 => KConst.F64(BitConverter.Int64BitsToDouble(reader.ReadInt64())),
			KConstKind.Char => KConst.Char((char)reader.ReadUInt16()),
			KConstKind.Str => KConst.Str(ReadString(reader)),
			_ => throw new KbcFormatException("повреждённый .kbc: константа неизвестного вида"),
		};
	}

	private static TypeSig ReadSig(BinaryReader reader)
	{
		string canonical = ReadString(reader);
		byte tag = reader.ReadByte();
		TypeSig sig = tag switch
		{
			1 => ReadPrimSig(reader),
			2 => new TypeSig.NamedSig(ReadString(reader), ReadSigList(reader)),
			3 => new TypeSig.ParamSig(reader.ReadInt32(), reader.ReadInt32(), ReadString(reader)),
			4 => new TypeSig.FuncSig(ReadSigList(reader), ReadSig(reader)),
			5 => ReadTupleSig(reader),
			6 => new TypeSig.NullableSig(ReadSig(reader)),
			7 => new TypeSig.UnionSig(ReadSigList(reader)),
			_ => throw new KbcFormatException($"повреждённый .kbc: неизвестный вид подписи типа {tag}"),
		};
		if (!string.Equals(sig.Canonical, canonical, StringComparison.Ordinal))
			throw new KbcFormatException(
				$"повреждённый .kbc: канонический текст подписи '{canonical}' не совпадает со структурой '{sig.Canonical}'");
		return sig;
	}

	private static TypeSig ReadPrimSig(BinaryReader reader)
	{
		byte raw = reader.ReadByte();
		PrimKind kind = (PrimKind)raw;
		if (!PrimNames.IsDefined(kind))
			throw new KbcFormatException($"повреждённый .kbc: неизвестный базовый тип {raw}");
		return TypeSig.Prim(kind);
	}

	private static TypeSig ReadTupleSig(BinaryReader reader)
	{
		TypeSig[] items = ReadSigList(reader);
		string?[]? names = null;
		if (reader.ReadBoolean())
		{
			names = new string?[items.Length];
			for (int i = 0; i < names.Length; i++) names[i] = ReadNullableString(reader);
		}
		return new TypeSig.TupleSig(items, names);
	}

	private static TypeSig[] ReadSigList(BinaryReader reader) =>
		[.. ReadList(reader, ReadSig, "подписей типов")];

	private static AttributeUse ReadAttribute(BinaryReader reader) =>
		new()
		{
			Name = ReadString(reader),
			Args = ReadList(reader, ReadConst, "аргументов атрибута"),
		};

	private static FieldDef ReadField(BinaryReader reader) =>
		new()
		{
			Name = ReadString(reader),
			Type = ReadSig(reader),
			Flags = (MemberFlags)reader.ReadUInt32(),
			Slot = reader.ReadInt32(),
			Attributes = ReadList(reader, ReadAttribute, "атрибутов"),
		};

	private static PropertyDef ReadProperty(BinaryReader reader) =>
		new()
		{
			Name = ReadString(reader),
			Type = ReadSig(reader),
			Flags = (MemberFlags)reader.ReadUInt32(),
			OwnerType = reader.ReadInt32(),
			Getter = reader.ReadInt32(),
			Setter = reader.ReadInt32(),
			Init = reader.ReadInt32(),
			BackingField = reader.ReadInt32(),
			Attributes = ReadList(reader, ReadAttribute, "атрибутов"),
		};

	private static FunctionDef ReadFunction(BinaryReader reader)
	{
		string name = ReadString(reader);
		int owner = reader.ReadInt32();
		MemberFlags flags = (MemberFlags)reader.ReadUInt32();
		TypeSig[] parameters = ReadSigList(reader);
		TypeSig returnType = ReadSig(reader);
		int localCount = reader.ReadInt32();
		int maxStack = reader.ReadInt32();
		int[] code = ReadIntArray(reader);
		int lineCount = ReadCount(reader, "записей таблицы строк");
		List<LineEntry> lines = new(lineCount);
		for (int i = 0; i < lineCount; i++) lines.Add(new LineEntry(reader.ReadInt32(), reader.ReadInt32()));
		return new FunctionDef
		{
			Name = name,
			OwnerType = owner,
			Flags = flags,
			ParamTypes = parameters,
			ReturnType = returnType,
			LocalCount = localCount,
			MaxStack = maxStack,
			Code = code,
			LineTable = lines,
			SourceFile = ReadNullableString(reader),
			NativeKey = ReadNullableString(reader),
			NativeTypeArgs = reader.ReadInt32(),
			Attributes = ReadList(reader, ReadAttribute, "атрибутов"),
		};
	}

	private static TypeDef ReadType(BinaryReader reader)
	{
		string name = ReadString(reader);
		string fullName = ReadString(reader);
		byte rawKind = reader.ReadByte();
		if (rawKind > (byte)TypeDefKind.Union)
			throw new KbcFormatException($"повреждённый .kbc: неизвестный вид типа {rawKind}");
		TypeDef type = new()
		{
			Name = name,
			FullName = fullName,
			Kind = (TypeDefKind)rawKind,
			Flags = (MemberFlags)reader.ReadUInt32(),
			GenericParams = ReadStrings(reader),
			Parents = ReadList(reader, ReadSig, "подписей типов"),
			Fields = ReadList(reader, ReadField, "полей"),
			Properties = ReadList(reader, ReadProperty, "свойств"),
			Methods = ReadList(reader, r => r.ReadInt32(), "методов"),
			Ctors = ReadList(reader, r => r.ReadInt32(), "конструкторов"),
			Attributes = ReadList(reader, ReadAttribute, "атрибутов"),
			UnionMembers = ReadList(reader, ReadSig, "членов union"),
		};
		return type;
	}
}
