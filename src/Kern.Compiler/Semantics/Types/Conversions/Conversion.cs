namespace Kern.Compiler.Semantics.Types.Conversions;

/// <summary>
/// Классифицированное преобразование. <see cref="Rank"/> нужен выбору перегрузки:
/// меньше — лучше. Все преобразования этого слоя неявные.
/// </summary>
public readonly record struct Conversion(ConversionKind Kind, bool IsImplicit)
{
	/// <summary>Стоимость преобразования для выбора перегрузки (меньше — лучше).</summary>
	public int Rank => Kind switch
	{
		ConversionKind.Identity => 0,
		ConversionKind.NumericConstant => 1,
		ConversionKind.NumericWidening => 2,
		ConversionKind.NullableWrap or ConversionKind.NilToNullable => 3,
		ConversionKind.BaseUpcast or ConversionKind.CollectionUpcast or ConversionKind.Lifted or ConversionKind.UnitImmutableErase => 4,
		ConversionKind.TupleUpcast or ConversionKind.FunctionUpcast => 5,
		ConversionKind.UnionUpcast or ConversionKind.UnionTarget => 6,
		ConversionKind.Boxing => 7,
		ConversionKind.StrToListChar or ConversionKind.ListCharToStr => 8,
		ConversionKind.NeverSource => 0,
		ConversionKind.ErrorSource => 99,
		_ => 99
	};

	public static Conversion Implicit(ConversionKind kind) => new(kind, true);
}
