namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Таблица имён примитивов и встроенных generic-типов. Общей изменяемой статики нет:
/// оба словаря создаются один раз и доступны только на чтение.
/// </summary>
public static class TypeCatalog
{
	private static readonly Dictionary<string, PrimitiveKind> Primitives = new(StringComparer.Ordinal)
	{
		["i8"] = PrimitiveKind.I8,
		["i16"] = PrimitiveKind.I16,
		["i32"] = PrimitiveKind.I32,
		["i64"] = PrimitiveKind.I64,
		["u8"] = PrimitiveKind.U8,
		["u16"] = PrimitiveKind.U16,
		["u32"] = PrimitiveKind.U32,
		["u64"] = PrimitiveKind.U64,
		["f16"] = PrimitiveKind.F16,
		["f32"] = PrimitiveKind.F32,
		["f64"] = PrimitiveKind.F64,
		["bool"] = PrimitiveKind.Bool,
		["char"] = PrimitiveKind.Char,
		["str"] = PrimitiveKind.Str,
		["void"] = PrimitiveKind.Void,
		["object"] = PrimitiveKind.Object,
		["inum"] = PrimitiveKind.Inum,
		["fnum"] = PrimitiveKind.Fnum,
		["num"] = PrimitiveKind.Num,
		["never"] = PrimitiveKind.Never
	};

	private static readonly Dictionary<string, BuiltinGenericKind> Builtins = CreateBuiltins();

	public static IReadOnlyCollection<string> PrimitiveNames => [.. Primitives.Keys];

	public static IReadOnlyCollection<string> BuiltinNames => [.. Builtins.Keys];

	public static bool TryGetPrimitive(string name, out PrimitiveType type)
	{
		if (Primitives.TryGetValue(name, out var kind))
		{
			type = PrimitiveType.For(kind);
			return true;
		}
		type = null!;
		return false;
	}

	public static bool TryGetPrimitiveKind(string name, out PrimitiveKind kind) =>
		Primitives.TryGetValue(name, out kind);

	public static PrimitiveType GetPrimitive(string name) =>
		TryGetPrimitive(name, out var primitive)
			? primitive
			: throw new ArgumentException($"Неизвестный примитивный тип '{name}'", nameof(name));

	public static bool TryGetBuiltin(string name, out BuiltinGenericKind kind) =>
		Builtins.TryGetValue(name, out kind);

	private static Dictionary<string, BuiltinGenericKind> CreateBuiltins()
	{
		var builtins = new Dictionary<string, BuiltinGenericKind>(StringComparer.Ordinal);
		foreach (var kind in Enum.GetValues<BuiltinGenericKind>())
			builtins[BuiltinGenericType.GetCanonicalName(kind)] = kind;

		// Устаревшее написание из spec §14; канонический текст — dict.
		builtins["map"] = BuiltinGenericKind.Dict;
		return builtins;
	}
}
