using Kern.Vm.Execution;

namespace Kern.Lib.Ffi.Native;

internal enum NativeKind
{
	Void,
	Bool,
	I8,
	I16,
	I32,
	I64,
	U8,
	U16,
	U32,
	U64,
	F32,
	F64,
	Ptr,
	Str,
}

/// <summary>
/// FFI-сигнатура <c>"i32(i32, str)"</c>: возвращаемый тип и типы аргументов.
/// Поддерживаются void, bool, i8/i16/i32/i64, u8/u16/u32/u64, f32/f64, ptr, str.
/// </summary>
internal sealed record NativeSignature(NativeKind Return, NativeKind[] Params)
{
	public static NativeSignature Parse(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
			throw new KernPanic(
				"ffi: пустая сигнатура; ожидается вид \"i32(i32, str)\"");
		string value = text.Trim();
		int open = value.IndexOf('(');
		int close = value.LastIndexOf(')');
		if (open < 0 || close < open || close != value.Length - 1)
			throw new KernPanic(
				$"ffi: неверная сигнатура \"{text}\"; ожидается вид \"i32(i32, str)\"");
		NativeKind result = Kind(value[..open].Trim());
		string inner = value[(open + 1)..close].Trim();
		NativeKind[] parameters = inner.Length == 0
			? []
			: [.. inner.Split(',', StringSplitOptions.TrimEntries).Select(Kind)];
		if (parameters.Any(parameter => parameter == NativeKind.Void))
			throw new KernPanic("ffi: void не может быть типом аргумента");
		return new NativeSignature(result, parameters);
	}

	private static NativeKind Kind(string name) => name switch
	{
		"void" => NativeKind.Void,
		"bool" => NativeKind.Bool,
		"i8" => NativeKind.I8,
		"i16" => NativeKind.I16,
		"i32" => NativeKind.I32,
		"i64" => NativeKind.I64,
		"u8" => NativeKind.U8,
		"u16" => NativeKind.U16,
		"u32" => NativeKind.U32,
		"u64" => NativeKind.U64,
		"f32" => NativeKind.F32,
		"f64" => NativeKind.F64,
		"ptr" => NativeKind.Ptr,
		"str" => NativeKind.Str,
		_ => throw new KernPanic(
			$"ffi: неизвестный тип '{name}'; допустимы: void bool i8 i16 i32 i64 u8 u16 u32 u64 f32 f64 ptr str"),
	};

	public override string ToString() =>
		$"{Name(Return)}({string.Join(", ", Params.Select(Name))})";

	public static Type Clr(NativeKind kind) => kind switch
	{
		NativeKind.Void => typeof(void),
		NativeKind.Bool => typeof(byte),
		NativeKind.I8 => typeof(sbyte),
		NativeKind.I16 => typeof(short),
		NativeKind.I32 => typeof(int),
		NativeKind.I64 => typeof(long),
		NativeKind.U8 => typeof(byte),
		NativeKind.U16 => typeof(ushort),
		NativeKind.U32 => typeof(uint),
		NativeKind.U64 => typeof(ulong),
		NativeKind.F32 => typeof(float),
		NativeKind.F64 => typeof(double),
		_ => typeof(nint),
	};

	private static string Name(NativeKind kind) => kind switch
	{
		NativeKind.Void => "void",
		NativeKind.Bool => "bool",
		NativeKind.I8 => "i8",
		NativeKind.I16 => "i16",
		NativeKind.I32 => "i32",
		NativeKind.I64 => "i64",
		NativeKind.U8 => "u8",
		NativeKind.U16 => "u16",
		NativeKind.U32 => "u32",
		NativeKind.U64 => "u64",
		NativeKind.F32 => "f32",
		NativeKind.F64 => "f64",
		NativeKind.Ptr => "ptr",
		_ => "str",
	};
}
