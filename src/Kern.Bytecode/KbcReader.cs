using System.IO.Compression;
using System.Text;

namespace Kern.Bytecode;

/// <summary>
/// Бинарное чтение <see cref="KbcModule"/> (формат KBC2; версии 2 и 3, little-endian).
/// Версия 3 компактнее (LEB128, без дублирующего канонического текста) и может быть
/// сжата Brotli; версия 2 читается для старых файлов. Любое повреждение файла даёт
/// <see cref="KbcFormatException"/> с русским сообщением, а не выход за границы массива
/// или падение внутри VM.
/// </summary>
public static class KbcReader
{
	private const int MaxItems = 1_000_000;
	private const int MaxStringBytes = 64 * 1024 * 1024;
	private const int MaxPayloadBytes = 512 * 1024 * 1024;
	private const byte FlagCompressed = 1;
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
		catch (InvalidDataException ex)
		{
			throw new KbcFormatException("повреждённый .kbc: не удалось распаковать данные", ex);
		}
	}

	private static KbcModule ReadCore(Stream stream)
	{
		using BinaryReader reader = new(stream, StrictUtf8, leaveOpen: true);

		byte[] magic = reader.ReadBytes(4);
		if (magic.Length != 4 || !magic.AsSpan().SequenceEqual("KBC2"u8))
			throw new KbcFormatException("это не модуль Kern: неверная сигнатура (ожидалось KBC2)");

		ushort version = reader.ReadUInt16();
		if (version is < 2 or > KbcModule.Version)
			throw new KbcFormatException(
				$"неподдерживаемая версия .kbc: {version}; поддерживаются 2..{KbcModule.Version}");

		if (version == 2)
			return ReadPayload(new PayloadReader(reader, compact: false, version));

		byte flags = reader.ReadByte();
		if ((flags & ~FlagCompressed) != 0)
			throw new KbcFormatException($"повреждённый .kbc: неизвестные флаги {flags}");

		int rawLength = reader.ReadInt32();
		if (rawLength < 0 || rawLength > MaxPayloadBytes)
			throw new KbcFormatException($"повреждённый .kbc: недопустимый размер данных {rawLength}");

		byte[] payload;
		if ((flags & FlagCompressed) != 0)
		{
			int compressedLength = reader.ReadInt32();
			if (compressedLength < 0 || compressedLength > MaxPayloadBytes)
				throw new KbcFormatException($"повреждённый .kbc: недопустимый размер сжатых данных {compressedLength}");
			byte[] compressed = ReadExactly(reader, compressedLength);
			using MemoryStream source = new(compressed, writable: false);
			using BrotliStream brotli = new(source, CompressionMode.Decompress);
			payload = new byte[rawLength];
			int read = 0;
			while (read < rawLength)
			{
				int chunk = brotli.Read(payload, read, rawLength - read);
				if (chunk == 0)
					throw new KbcFormatException("повреждённый .kbc: сжатые данные короче заявленного размера");
				read += chunk;
			}
			if (brotli.ReadByte() != -1)
				throw new KbcFormatException("повреждённый .kbc: сжатые данные длиннее заявленного размера");
		}
		else
		{
			payload = ReadExactly(reader, rawLength);
		}

		using MemoryStream body = new(payload, writable: false);
		using BinaryReader payloadReader = new(body, StrictUtf8, leaveOpen: true);
		return ReadPayload(new PayloadReader(payloadReader, compact: true, version));
	}

	private static byte[] ReadExactly(BinaryReader reader, int length)
	{
		byte[] bytes = reader.ReadBytes(length);
		if (bytes.Length != length)
			throw new KbcFormatException("неожиданный конец файла .kbc: данные обрезаны");
		return bytes;
	}

	private static KbcModule ReadPayload(PayloadReader reader)
	{
		KbcModule module = new() { Name = reader.String() };
		module.References = reader.Strings();
		module.StringPool = reader.Strings();
		module.ConstPool = reader.List(reader.Const, "констант");
		module.TypeSigPool = reader.List(reader.Sig, "подписей типов");
		module.TypeSigListPool = reader.List(reader.IntArray, "списков подписей");
		module.TypePool = reader.List(reader.Type, "типов");
		module.FieldPool = reader.List(reader.Field, "полей");
		module.PropertyPool = reader.List(reader.Property, "свойств");
		module.FunctionPool = reader.List(reader.Function, "функций");
		module.EntryPoint = reader.Int32();
		module.StaticInit = reader.Int32();
		module.Attributes = reader.List(reader.Attribute, "атрибутов");

		KbcModuleValidator.Validate(module);
		return module;
	}

	/// <summary>Читатель секций: компактное (v3) или классическое (v2) кодирование.</summary>
	private sealed class PayloadReader(BinaryReader reader, bool compact, ushort version)
	{
		public int Int32() => compact ? reader.ReadInt32Var() : reader.ReadInt32();

		public uint UInt32() => compact ? reader.ReadUInt32Var() : reader.ReadUInt32();

		public int Count(string what)
		{
			int count = compact ? (int)reader.ReadUInt32Var() : reader.ReadInt32();
			if (count < 0 || count > MaxItems)
				throw new KbcFormatException($"повреждённый .kbc: недопустимое количество {what}: {count}");
			return count;
		}

		public List<T> List<T>(Func<T> read, string what)
		{
			int count = Count(what);
			List<T> items = new(count);
			for (int i = 0; i < count; i++) items.Add(read());
			return items;
		}

		public List<string> Strings()
		{
			int count = Count("строк");
			List<string> items = new(count);
			for (int i = 0; i < count; i++) items.Add(String());
			return items;
		}

		public string String()
		{
			int length = compact ? (int)reader.ReadUInt32Var() : reader.ReadInt32();
			if (length < 0 || length > MaxStringBytes)
				throw new KbcFormatException($"повреждённый .kbc: недопустимая длина строки {length}");
			byte[] bytes = ReadExactly(reader, length);
			try
			{
				return StrictUtf8.GetString(bytes);
			}
			catch (DecoderFallbackException ex)
			{
				throw new KbcFormatException("повреждённый .kbc: строка не в кодировке UTF-8", ex);
			}
		}

		public string? NullableString() => reader.ReadBoolean() ? String() : null;

		public int[] IntArray()
		{
			int count = Count("элементов массива");
			int[] items = new int[count];
			for (int i = 0; i < count; i++) items[i] = Int32();
			return items;
		}

		public bool Bool()
		{
			byte value = reader.ReadByte();
			if (value > 1)
				throw new KbcFormatException($"повреждённый .kbc: недопустимое значение bool {value}");
			return value != 0;
		}

		public KConst Const()
		{
			byte raw = reader.ReadByte();
			if (raw > (byte)KConstKind.Str)
				throw new KbcFormatException($"повреждённый .kbc: неизвестный вид константы {raw}");
			return (KConstKind)raw switch
			{
				KConstKind.Nil => KConst.Nil,
				KConstKind.None => KConst.None,
				KConstKind.Void => KConst.Void,
				KConstKind.Bool => KConst.Bool(Bool()),
				KConstKind.I64 => KConst.I64(compact ? reader.ReadInt64Var() : reader.ReadInt64()),
				KConstKind.F64 => KConst.F64(BitConverter.Int64BitsToDouble(reader.ReadInt64())),
				KConstKind.Char => KConst.Char((char)(compact ? reader.ReadUInt32Var() : reader.ReadUInt16())),
				KConstKind.Str => KConst.Str(String()),
				_ => throw new KbcFormatException("повреждённый .kbc: константа неизвестного вида"),
			};
		}

		public TypeSig Sig()
		{
			string? canonical = compact ? null : String();
			byte tag = reader.ReadByte();
			TypeSig sig = tag switch
			{
				1 => ReadPrimSig(),
				2 => new TypeSig.NamedSig(String(), SigArray()),
				3 => new TypeSig.ParamSig(Int32(), Int32(), String()),
				4 => new TypeSig.FuncSig(SigArray(), Sig()),
				5 => ReadTupleSig(),
				6 => new TypeSig.NullableSig(Sig()),
				7 => new TypeSig.UnionSig(SigArray()),
				_ => throw new KbcFormatException($"повреждённый .kbc: неизвестный вид подписи типа {tag}"),
			};
			if (canonical is not null && !string.Equals(sig.Canonical, canonical, StringComparison.Ordinal))
				throw new KbcFormatException(
					$"повреждённый .kbc: канонический текст подписи '{canonical}' не совпадает со структурой '{sig.Canonical}'");
			return sig;
		}

		private TypeSig ReadPrimSig()
		{
			byte raw = reader.ReadByte();
			PrimKind kind = (PrimKind)raw;
			if (!PrimNames.IsDefined(kind))
				throw new KbcFormatException($"повреждённый .kbc: неизвестный базовый тип {raw}");
			return TypeSig.Prim(kind);
		}

		private TypeSig ReadTupleSig()
		{
			TypeSig[] items = SigArray();
			string?[]? names = null;
			if (reader.ReadBoolean())
			{
				names = new string?[items.Length];
				for (int i = 0; i < names.Length; i++) names[i] = NullableString();
			}
			return new TypeSig.TupleSig(items, names);
		}

		private TypeSig[] SigArray() => [.. List(Sig, "подписей типов")];

		public AttributeUse Attribute() =>
			new()
			{
				Name = String(),
				Args = List(Const, "аргументов атрибута"),
			};

		public FieldDef Field() =>
			new()
			{
				Name = String(),
				Type = Sig(),
				Flags = (MemberFlags)UInt32(),
				Slot = Int32(),
				Attributes = List(Attribute, "атрибутов"),
			};

		public PropertyDef Property() =>
			new()
			{
				Name = String(),
				Type = Sig(),
				Flags = (MemberFlags)UInt32(),
				OwnerType = Int32(),
				Getter = Int32(),
				Setter = Int32(),
				Init = Int32(),
				BackingField = Int32(),
				Attributes = List(Attribute, "атрибутов"),
			};

		public FunctionDef Function()
		{
			string name = String();
			int owner = Int32();
			MemberFlags flags = (MemberFlags)UInt32();
			TypeSig[] parameters = SigArray();
			TypeSig returnType = Sig();
			int localCount = Int32();
			int maxStack = Int32();
			int[] code = IntArray();
			int lineCount = Count("записей таблицы строк");
			List<LineEntry> lines = new(lineCount);
			for (int i = 0; i < lineCount; i++) lines.Add(new LineEntry(Int32(), Int32()));
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
				SourceFile = NullableString(),
				LocalNames = version >= 4 ? Strings() : [],
				NativeKey = NullableString(),
				NativeTypeArgs = Int32(),
				Attributes = List(Attribute, "атрибутов"),
			};
		}

		public TypeDef Type()
		{
			string name = String();
			string fullName = String();
			byte rawKind = reader.ReadByte();
			if (rawKind > (byte)TypeDefKind.Union)
				throw new KbcFormatException($"повреждённый .kbc: неизвестный вид типа {rawKind}");
			return new TypeDef
			{
				Name = name,
				FullName = fullName,
				Kind = (TypeDefKind)rawKind,
				Flags = (MemberFlags)UInt32(),
				GenericParams = Strings(),
				Parents = List(Sig, "подписей типов"),
				Fields = List(Field, "полей"),
				Properties = List(Property, "свойств"),
				Methods = List(Int32, "методов"),
				Ctors = List(Int32, "конструкторов"),
				Attributes = List(Attribute, "атрибутов"),
				UnionMembers = List(Sig, "членов union"),
			};
		}
	}
}

/// <summary>LEB128-примитивы для компактной версии формата.</summary>
internal static class BinaryVarint
{
	public static uint ReadUInt32Var(this BinaryReader reader)
	{
		uint result = 0;
		int shift = 0;
		while (true)
		{
			byte value = reader.ReadByte();
			result |= (uint)(value & 0x7F) << shift;
			if ((value & 0x80) == 0) return result;
			shift += 7;
			if (shift > 35)
				throw new KbcFormatException("повреждённый .kbc: слишком длинное целое LEB128");
		}
	}

	public static int ReadInt32Var(this BinaryReader reader)
	{
		uint value = reader.ReadUInt32Var();
		return (int)(value >> 1) ^ -(int)(value & 1);
	}

	public static long ReadInt64Var(this BinaryReader reader)
	{
		ulong result = 0;
		int shift = 0;
		while (true)
		{
			byte value = reader.ReadByte();
			result |= (ulong)(value & 0x7F) << shift;
			if ((value & 0x80) == 0) return (long)(result >> 1) ^ -(long)(result & 1);
			shift += 7;
			if (shift > 70)
				throw new KbcFormatException("повреждённый .kbc: слишком длинное целое LEB128");
		}
	}
}
