namespace Kern.Lib.Ffi.Native;

internal enum NativeKind { Void, Bool, I8, I16, I32, I64, U8, U16, U32, U64, F32, F64, Ptr, Str }

/// <summary>Разобранная сигнатура вида <c>"i32(i32, str)"</c>.</summary>
internal sealed record NativeSignature(NativeKind Return, NativeKind[] Params)
{
	public static NativeSignature Parse(string text)
	{
		int open = text.IndexOf('(');
		int close = text.LastIndexOf(')');
		if (open < 0 || close < open) throw new FormatException($"сигнатура должна быть вида \"i32(i32, str)\", получено \"{text}\"");
		var ret = Kind(text[..open].Trim());
		var inner = text[(open + 1)..close].Trim();
		var ps = inner.Length == 0 ? [] : inner.Split(',', StringSplitOptions.TrimEntries).Select(Kind).ToArray();
		if (ps.Any(p => p == NativeKind.Void)) throw new FormatException("void допустим только как тип результата");
		return new NativeSignature(ret, ps);
	}

	private static NativeKind Kind(string name) => name switch
	{
		"void" => NativeKind.Void, "bool" => NativeKind.Bool,
		"i8" => NativeKind.I8, "i16" => NativeKind.I16, "i32" => NativeKind.I32, "i64" => NativeKind.I64,
		"u8" => NativeKind.U8, "u16" => NativeKind.U16, "u32" => NativeKind.U32, "u64" => NativeKind.U64,
		"f32" => NativeKind.F32, "f64" => NativeKind.F64, "ptr" => NativeKind.Ptr, "str" => NativeKind.Str,
		_ => throw new FormatException($"неизвестный тип '{name}' (допустимы: void bool i8 i16 i32 i64 u8 u16 u32 u64 f32 f64 ptr str)"),
	};

	/// <summary>CLR-тип параметра/результата в `calli`: bool — байт, ptr и str — указатель.</summary>
	public static Type Clr(NativeKind k) => k switch
	{
		NativeKind.Void => typeof(void), NativeKind.Bool => typeof(byte),
		NativeKind.I8 => typeof(sbyte), NativeKind.I16 => typeof(short), NativeKind.I32 => typeof(int), NativeKind.I64 => typeof(long),
		NativeKind.U8 => typeof(byte), NativeKind.U16 => typeof(ushort), NativeKind.U32 => typeof(uint), NativeKind.U64 => typeof(ulong),
		NativeKind.F32 => typeof(float), NativeKind.F64 => typeof(double),
		_ => typeof(nint),
	};
}
