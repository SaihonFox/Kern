namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Примитивные типы из spec §3. <see cref="None"/> — внутреннее состояние потока,
/// по имени не разрешается; <c>nil</c>/<c>none</c> являются значениями, а не типами.
/// </summary>
public enum PrimitiveKind
{
	I8,
	I16,
	I32,
	I64,
	U8,
	U16,
	U32,
	U64,
	F16,
	F32,
	F64,
	Bool,
	Char,
	Str,
	Void,
	Object,
	Inum,
	Fnum,
	Num,
	Never,
	None
}

/// <summary>Единственное место, где вид примитива превращается в текст.</summary>
internal static class PrimitiveNames
{
	public static string Get(PrimitiveKind kind) => kind switch
	{
		PrimitiveKind.I8 => "i8",
		PrimitiveKind.I16 => "i16",
		PrimitiveKind.I32 => "i32",
		PrimitiveKind.I64 => "i64",
		PrimitiveKind.U8 => "u8",
		PrimitiveKind.U16 => "u16",
		PrimitiveKind.U32 => "u32",
		PrimitiveKind.U64 => "u64",
		PrimitiveKind.F16 => "f16",
		PrimitiveKind.F32 => "f32",
		PrimitiveKind.F64 => "f64",
		PrimitiveKind.Bool => "bool",
		PrimitiveKind.Char => "char",
		PrimitiveKind.Str => "str",
		PrimitiveKind.Void => "void",
		PrimitiveKind.Object => "object",
		PrimitiveKind.Inum => "inum",
		PrimitiveKind.Fnum => "fnum",
		PrimitiveKind.Num => "num",
		PrimitiveKind.Never => "never",
		PrimitiveKind.None => "none",
		_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный вид примитива")
	};
}
