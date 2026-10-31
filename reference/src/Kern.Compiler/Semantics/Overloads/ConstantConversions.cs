using System.Numerics;
using Kern.Compiler.Lexing;
using Kern.Compiler.Semantics.Types;
using Kern.Bytecode;

namespace Kern.Compiler.Semantics.Overloads;

/// <summary>
/// Константное приведение числового литерала БЕЗ суффикса (как implicit constant expression conversion в C#):
/// `5` → u8/i64/f32…, если значение вписывается в целевой тип. Литерал с суффиксом (`5u32`, `5b`) имеет точный тип и
/// приводится только обычными неявными преобразованиями. Правило изолировано здесь, чтобы его можно было изменить/убрать.
/// </summary>
public static class ConstantConversions
{
	public static bool CanConvert(NumberValue literal, TypeSymbol target)
	{
		if (literal.Suffix is not null) return false;
		if (target is NullableType n) target = n.Inner;
		if (target is not PrimitiveType p) return false;
		if (p.IsUniversalNumeric) return p.Kind switch
		{
			PrimKind.INum => !literal.IsFloat,
			PrimKind.FNum => literal.IsFloat,
			_ => true,
		};
		if (!p.IsNumeric) return false;
		if (literal.IsFloat) return p.IsFloat; // дробный литерал в целый — никогда
		if (p.IsFloat) return true;            // целый литерал в float: точность — на совести программиста (как в C#)
		var (min, max) = Range(p);
		return literal.Integer >= min && literal.Integer <= max;
	}

	private static (BigInteger Min, BigInteger Max) Range(PrimitiveType p) => p.Kind switch
	{
		PrimKind.I8 => (sbyte.MinValue, sbyte.MaxValue),
		PrimKind.I16 => (short.MinValue, short.MaxValue),
		PrimKind.I32 => (int.MinValue, int.MaxValue),
		PrimKind.I64 => (long.MinValue, long.MaxValue),
		PrimKind.U8 => (byte.MinValue, byte.MaxValue),
		PrimKind.U16 => (ushort.MinValue, ushort.MaxValue),
		PrimKind.U32 => (uint.MinValue, uint.MaxValue),
		PrimKind.U64 => (ulong.MinValue, ulong.MaxValue),
		_ => (BigInteger.Zero, BigInteger.Zero),
	};
}
