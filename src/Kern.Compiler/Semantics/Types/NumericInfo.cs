namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Числовые характеристики примитива. Универсальные <c>inum</c>/<c>fnum</c>/<c>num</c>
/// не имеют фиксированной ширины (<see cref="Bits"/> = 0).
/// </summary>
public sealed record NumericInfo(
	PrimitiveKind Kind,
	bool IsInteger,
	bool IsFloat,
	bool IsSigned,
	bool IsUniversal,
	int Bits)
{
	public static bool TryGet(PrimitiveKind kind, out NumericInfo info)
	{
		switch (kind)
		{
			case PrimitiveKind.I8:
				info = new(kind, true, false, true, false, 8);
				return true;
			case PrimitiveKind.I16:
				info = new(kind, true, false, true, false, 16);
				return true;
			case PrimitiveKind.I32:
				info = new(kind, true, false, true, false, 32);
				return true;
			case PrimitiveKind.I64:
				info = new(kind, true, false, true, false, 64);
				return true;
			case PrimitiveKind.U8:
				info = new(kind, true, false, false, false, 8);
				return true;
			case PrimitiveKind.U16:
				info = new(kind, true, false, false, false, 16);
				return true;
			case PrimitiveKind.U32:
				info = new(kind, true, false, false, false, 32);
				return true;
			case PrimitiveKind.U64:
				info = new(kind, true, false, false, false, 64);
				return true;
			case PrimitiveKind.F16:
				info = new(kind, false, true, true, false, 16);
				return true;
			case PrimitiveKind.F32:
				info = new(kind, false, true, true, false, 32);
				return true;
			case PrimitiveKind.F64:
				info = new(kind, false, true, true, false, 64);
				return true;
			case PrimitiveKind.Inum:
				info = new(kind, true, false, true, true, 0);
				return true;
			case PrimitiveKind.Fnum:
				info = new(kind, false, true, true, true, 0);
				return true;
			case PrimitiveKind.Num:
				info = new(kind, false, false, true, true, 0);
				return true;
			default:
				info = null!;
				return false;
		}
	}

	/// <summary>Принимает ли тип число: конкретный числовой или универсальный <c>inum</c>/<c>fnum</c>/<c>num</c>.</summary>
	public static bool IsNumeric(KernType type) =>
		type is PrimitiveType primitive && TryGet(primitive.Primitive, out _);
}
