using System.Numerics;

namespace Kern.Compiler.Semantics.Types.Conversions;

/// <summary>
/// Числовая арифметика преобразований: неявные расширения (правило C#, spec §15) и проверки
/// диапазонов для константных литералов. Только чистые функции, без состояния.
/// </summary>
internal static class NumericRules
{
	/// <summary>
	/// Неявное числовое расширение по C#. <c>char</c>/<c>bool</c> не числовые, сужения нет.
	/// Универсальные <c>inum</c>/<c>fnum</c>/<c>num</c> принимают значения своего семейства
	/// (docs/design/M2-types.md §4).
	/// </summary>
	public static bool IsWidening(PrimitiveKind from, PrimitiveKind to) => (from, to) switch
	{
		(PrimitiveKind.I8, PrimitiveKind.I16 or PrimitiveKind.I32 or PrimitiveKind.I64 or PrimitiveKind.F32 or PrimitiveKind.F64) => true,
		(PrimitiveKind.I16, PrimitiveKind.I32 or PrimitiveKind.I64 or PrimitiveKind.F32 or PrimitiveKind.F64) => true,
		(PrimitiveKind.I32, PrimitiveKind.I64 or PrimitiveKind.F32 or PrimitiveKind.F64) => true,
		(PrimitiveKind.I64, PrimitiveKind.F32 or PrimitiveKind.F64) => true,
		(PrimitiveKind.U8, PrimitiveKind.U16 or PrimitiveKind.U32 or PrimitiveKind.U64 or
			PrimitiveKind.I16 or PrimitiveKind.I32 or PrimitiveKind.I64 or PrimitiveKind.F32 or PrimitiveKind.F64) => true,
		(PrimitiveKind.U16, PrimitiveKind.U32 or PrimitiveKind.U64 or
			PrimitiveKind.I32 or PrimitiveKind.I64 or PrimitiveKind.F32 or PrimitiveKind.F64) => true,
		(PrimitiveKind.U32, PrimitiveKind.U64 or PrimitiveKind.I64 or PrimitiveKind.F32 or PrimitiveKind.F64) => true,
		(PrimitiveKind.U64, PrimitiveKind.F32 or PrimitiveKind.F64) => true,
		(PrimitiveKind.F16, PrimitiveKind.F32 or PrimitiveKind.F64) => true,
		(PrimitiveKind.F32, PrimitiveKind.F64) => true,
		(PrimitiveKind.Inum, PrimitiveKind.Num) => true,
		(PrimitiveKind.Fnum, PrimitiveKind.Num) => true,
		(_, PrimitiveKind.Inum) => IsIntegral(from),
		(_, PrimitiveKind.Fnum) => IsFloat(from),
		(_, PrimitiveKind.Num) => IsIntegral(from) || IsFloat(from),
		_ => false
	};

	public static bool IsIntegral(PrimitiveKind kind) => kind is
		PrimitiveKind.I8 or PrimitiveKind.I16 or PrimitiveKind.I32 or PrimitiveKind.I64 or
		PrimitiveKind.U8 or PrimitiveKind.U16 or PrimitiveKind.U32 or PrimitiveKind.U64 or
		PrimitiveKind.Inum;

	public static bool IsFloat(PrimitiveKind kind) => kind is
		PrimitiveKind.F16 or PrimitiveKind.F32 or PrimitiveKind.F64 or PrimitiveKind.Fnum;

	/// <summary>Помещается ли целое значение в целевой целый тип (включая универсальные).</summary>
	public static bool FitsInteger(BigInteger value, PrimitiveKind kind) => kind switch
	{
		PrimitiveKind.I8 => value >= sbyte.MinValue && value <= sbyte.MaxValue,
		PrimitiveKind.I16 => value >= short.MinValue && value <= short.MaxValue,
		PrimitiveKind.I32 => value >= int.MinValue && value <= int.MaxValue,
		PrimitiveKind.I64 => value >= long.MinValue && value <= long.MaxValue,
		PrimitiveKind.U8 => value >= byte.MinValue && value <= byte.MaxValue,
		PrimitiveKind.U16 => value >= ushort.MinValue && value <= ushort.MaxValue,
		PrimitiveKind.U32 => value >= uint.MinValue && value <= uint.MaxValue,
		PrimitiveKind.U64 => value >= ulong.MinValue && value <= ulong.MaxValue,
		PrimitiveKind.Inum or PrimitiveKind.Num => true,
		_ => false
	};

	/// <summary>Помещается ли вещественное значение в целевой тип (по диапазону, без проверки точности).</summary>
	public static bool FitsFloat(double value, PrimitiveKind kind)
	{
		if (!double.IsFinite(value))
			return false;

		return kind switch
		{
			PrimitiveKind.F16 => System.Math.Abs(value) <= 65504.0,
			PrimitiveKind.F32 => System.Math.Abs(value) <= float.MaxValue,
			PrimitiveKind.F64 or PrimitiveKind.Fnum or PrimitiveKind.Num => true,
			_ => false
		};
	}

	/// <summary>
	/// Целый литерал в вещественный тип: проверяется диапазон; потеря точности допускается,
	/// как в C# (константа <c>int</c> неявно приводится к <c>float</c>).
	/// </summary>
	public static bool FitsIntegerAsFloat(BigInteger value, PrimitiveKind kind) =>
		FitsFloat((double)value, kind);
}
