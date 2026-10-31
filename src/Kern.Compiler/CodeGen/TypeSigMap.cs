using Kern.Compiler.Semantics.Types;
using Kern.Bytecode;

namespace Kern.Compiler.CodeGen;

/// <summary>Перевод типов binder'а в подписи .kbc (M4). Для неподдержанных типов — object.</summary>
internal static class TypeSigMap
{
	public static TypeSig From(KernType type) => From(type, "module");

	/// <summary>Подпись типа с учётом имени модуля для unit/class (M3-D).</summary>
	public static TypeSig From(KernType type, string moduleName) =>
		type switch
		{
			PrimitiveType value => TypeSig.Prim(Map(value.Primitive)),
			NilType => TypeSig.Nil,
			UnitType unit => Named(unit.Symbol, unit.Arguments, moduleName),
			ClassType @class => Named(@class.Symbol, @class.Arguments, moduleName),
			BuiltinGenericType builtin => new TypeSig.NamedSig(
				BuiltinGenericType.GetCanonicalName(builtin.BuiltinKind),
				[.. builtin.Arguments.Select(argument => From(argument, moduleName))]),
			NullableType nullable => new TypeSig.NullableSig(From(nullable.Underlying, moduleName)),
			_ => TypeSig.Object
		};

	public static string FullName(TypeSymbol symbol, string moduleName) =>
		$"{symbol.ModuleName ?? moduleName}.{symbol.Name}";

	private static TypeSig Named(TypeSymbol symbol, IReadOnlyList<KernType> arguments, string moduleName) =>
		new TypeSig.NamedSig(
			FullName(symbol, moduleName),
			[.. arguments.Select(argument => From(argument, moduleName))]);

	public static bool TryPrimitive(KernType type, out PrimitiveKind primitive)
	{
		if (type is PrimitiveType value)
		{
			primitive = value.Primitive;
			return true;
		}

		primitive = default;
		return false;
	}

	private static PrimKind Map(PrimitiveKind primitive) => primitive switch
	{
		PrimitiveKind.Void => PrimKind.Void,
		PrimitiveKind.Bool => PrimKind.Bool,
		PrimitiveKind.Char => PrimKind.Char,
		PrimitiveKind.Str => PrimKind.Str,
		PrimitiveKind.Object => PrimKind.Object,
		PrimitiveKind.None => PrimKind.None,
		PrimitiveKind.I8 => PrimKind.I8,
		PrimitiveKind.I16 => PrimKind.I16,
		PrimitiveKind.I32 => PrimKind.I32,
		PrimitiveKind.I64 => PrimKind.I64,
		PrimitiveKind.U8 => PrimKind.U8,
		PrimitiveKind.U16 => PrimKind.U16,
		PrimitiveKind.U32 => PrimKind.U32,
		PrimitiveKind.U64 => PrimKind.U64,
		PrimitiveKind.F16 => PrimKind.F16,
		PrimitiveKind.F32 => PrimKind.F32,
		PrimitiveKind.F64 => PrimKind.F64,
		PrimitiveKind.Inum => PrimKind.INum,
		PrimitiveKind.Fnum => PrimKind.FNum,
		PrimitiveKind.Num => PrimKind.Num,
		PrimitiveKind.Never => PrimKind.Never,
		_ => PrimKind.Object
	};
}
