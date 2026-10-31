namespace Kern.Compiler.Semantics.Types.Conversions;

/// <summary>Вид преобразования между разрешёнными типами (docs/design/M2-types.md §4).</summary>
public enum ConversionKind
{
	Identity,
	NumericWidening,
	NumericConstant,
	NullableWrap,
	Lifted,
	Boxing,
	UnionUpcast,
	UnionTarget,
	BaseUpcast,
	CollectionUpcast,
	UnitImmutableErase,
	StrToListChar,
	ListCharToStr,
	TupleUpcast,
	FunctionUpcast,
	NeverSource,
	ErrorSource,
	NilToNullable
}
