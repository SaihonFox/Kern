namespace Kern.Compiler.Semantics.Types;

/// <summary>Примитивный тип. Экземпляры — статические синглтоны, равные по <see cref="Primitive"/>.</summary>
public sealed record PrimitiveType : KernType
{
	public static readonly PrimitiveType I8 = new(PrimitiveKind.I8);
	public static readonly PrimitiveType I16 = new(PrimitiveKind.I16);
	public static readonly PrimitiveType I32 = new(PrimitiveKind.I32);
	public static readonly PrimitiveType I64 = new(PrimitiveKind.I64);
	public static readonly PrimitiveType U8 = new(PrimitiveKind.U8);
	public static readonly PrimitiveType U16 = new(PrimitiveKind.U16);
	public static readonly PrimitiveType U32 = new(PrimitiveKind.U32);
	public static readonly PrimitiveType U64 = new(PrimitiveKind.U64);
	public static readonly PrimitiveType F16 = new(PrimitiveKind.F16);
	public static readonly PrimitiveType F32 = new(PrimitiveKind.F32);
	public static readonly PrimitiveType F64 = new(PrimitiveKind.F64);
	public static readonly PrimitiveType Bool = new(PrimitiveKind.Bool);
	public static readonly PrimitiveType Char = new(PrimitiveKind.Char);
	public static readonly PrimitiveType Str = new(PrimitiveKind.Str);
	public static readonly PrimitiveType Void = new(PrimitiveKind.Void);
	public static readonly PrimitiveType Object = new(PrimitiveKind.Object);
	public static readonly PrimitiveType Inum = new(PrimitiveKind.Inum);
	public static readonly PrimitiveType Fnum = new(PrimitiveKind.Fnum);
	public static readonly PrimitiveType Num = new(PrimitiveKind.Num);
	public static readonly PrimitiveType Never = new(PrimitiveKind.Never);
	public static readonly PrimitiveType None = new(PrimitiveKind.None);

	private static readonly Dictionary<PrimitiveKind, PrimitiveType> ByKind = new()
	{
		[PrimitiveKind.I8] = I8,
		[PrimitiveKind.I16] = I16,
		[PrimitiveKind.I32] = I32,
		[PrimitiveKind.I64] = I64,
		[PrimitiveKind.U8] = U8,
		[PrimitiveKind.U16] = U16,
		[PrimitiveKind.U32] = U32,
		[PrimitiveKind.U64] = U64,
		[PrimitiveKind.F16] = F16,
		[PrimitiveKind.F32] = F32,
		[PrimitiveKind.F64] = F64,
		[PrimitiveKind.Bool] = Bool,
		[PrimitiveKind.Char] = Char,
		[PrimitiveKind.Str] = Str,
		[PrimitiveKind.Void] = Void,
		[PrimitiveKind.Object] = Object,
		[PrimitiveKind.Inum] = Inum,
		[PrimitiveKind.Fnum] = Fnum,
		[PrimitiveKind.Num] = Num,
		[PrimitiveKind.Never] = Never,
		[PrimitiveKind.None] = None
	};

	private PrimitiveType(PrimitiveKind primitive) : base(TypeKind.Primitive) => Primitive = primitive;

	public PrimitiveKind Primitive { get; }

	public static PrimitiveType For(PrimitiveKind kind) => ByKind[kind];

	public override string ToString() => PrimitiveNames.Get(Primitive);
}
