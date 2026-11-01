using Kern.Compiler.Semantics.Types;
using Kern.Bytecode;

namespace Kern.Compiler.CodeGen;

/// <summary>
/// Перевод типов binder'а в подписи .kbc (M4). Составные типы (nullable, кортеж, union,
/// функция) сохраняют структуру; неразрешимое на этом слое (ошибка binder'а) — object.
/// </summary>
internal static class TypeSigMap
{
	public static TypeSig From(KernType type) => From(type, "module", null);

	/// <summary>Подпись типа с учётом имени модуля для unit/class (M3-D).</summary>
	public static TypeSig From(KernType type, string moduleName) => From(type, moduleName, null);

	/// <summary>
	/// Подпись типа с параметрами типа владельца (<c>scope 0</c>). Нужна только для
	/// <c>TypeDef.Parents</c>: VM подставляет аргументы reified-типа в <see cref="TypeSig.ParamSig"/>
	/// при проверке «объект наследника является экземпляром родителя».
	/// <para>
	/// Вне родительской подписи параметр типа намеренно стирается в <c>object</c>:
	/// <see cref="TypeSig.ParamSig"/> требует scope и индекс, которых у <see cref="TypeParameterType"/>
	/// нет, а VM не разрешает параметры в подписях полей/функций без владельца.
	/// </para>
	/// </summary>
	public static TypeSig From(
		KernType type,
		string moduleName,
		IReadOnlyList<TypeParameterType>? typeParameters) =>
		type switch
		{
			PrimitiveType value => TypeSig.Prim(Map(value.Primitive)),
			NilType => TypeSig.Nil,
			UnitType unit => Named(unit.Symbol, unit.Arguments, moduleName, typeParameters),
			ClassType @class => Named(@class.Symbol, @class.Arguments, moduleName, typeParameters),
			BuiltinGenericType builtin => new TypeSig.NamedSig(
				BuiltinGenericType.GetCanonicalName(builtin.BuiltinKind),
				[.. builtin.Arguments.Select(argument => From(argument, moduleName, typeParameters))]),
			NullableType nullable => new TypeSig.NullableSig(From(nullable.Underlying, moduleName, typeParameters)),
			TupleType tuple => new TypeSig.TupleSig(
				[.. tuple.Elements.Select(element => From(element.Type, moduleName, typeParameters))],
				tuple.Elements.Any(element => element.Name is not null)
					? [.. tuple.Elements.Select(element => element.Name)]
					: null),
			UnionType union => new TypeSig.UnionSig(
				[.. union.Alternatives.Select(alternative => From(alternative, moduleName, typeParameters))]),
			FunctionType function => new TypeSig.FuncSig(
				[.. function.Parameters.Select(parameter => From(parameter, moduleName, typeParameters))],
				From(function.ReturnType, moduleName, typeParameters)),
			TypeParameterType parameter => Parameter(parameter, typeParameters),
			_ => TypeSig.Object
		};

	/// <summary>
	/// Параметр типа — <see cref="TypeSig.ParamSig"/> только при известном индексе среди
	/// параметров владельца; иначе <c>object</c> (см. комментарий у <see cref="From(KernType, string, IReadOnlyList{TypeParameterType}?)"/>).
	/// </summary>
	private static TypeSig Parameter(
		TypeParameterType parameter,
		IReadOnlyList<TypeParameterType>? typeParameters)
	{
		if (typeParameters is not null)
			for (var index = 0; index < typeParameters.Count; index++)
				if (string.Equals(typeParameters[index].Name, parameter.Name, StringComparison.Ordinal))
					return new TypeSig.ParamSig(0, index, parameter.Name);
		return TypeSig.Object;
	}

	public static string FullName(TypeSymbol symbol, string moduleName) =>
		$"{symbol.ModuleName ?? moduleName}.{symbol.Name}";

	private static TypeSig Named(
		TypeSymbol symbol,
		IReadOnlyList<KernType> arguments,
		string moduleName,
		IReadOnlyList<TypeParameterType>? typeParameters) =>
		new TypeSig.NamedSig(
			FullName(symbol, moduleName),
			[.. arguments.Select(argument => From(argument, moduleName, typeParameters))]);

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
