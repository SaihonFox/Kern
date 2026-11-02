namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Параметр обобщения. Равенство — только по имени: <c>T</c> из разных объявлений совпадает,
/// <c>T</c> и <c>U</c> — нет. Ограничения используются при проверке аргументов (slice E).
/// </summary>
public sealed record TypeParameterType : KernType
{
	public TypeParameterType(
		string name,
		IReadOnlyList<KernType> constraints,
		bool requiresConstructor = false,
		IReadOnlyList<KernType>? constructorParameters = null)
		: base(TypeKind.TypeParameter)
	{
		Name = name;
		Constraints = StructuralList<KernType>.From(constraints);
		RequiresConstructor = requiresConstructor;
		ConstructorParameters = constructorParameters is null
			? []
			: StructuralList<KernType>.From(constructorParameters);
	}

	public string Name { get; }

	public IReadOnlyList<KernType> Constraints { get; }

	/// <summary>
	/// Ограничение <c>ctor()</c> (spec §14): тип-аргумент обязан иметь конструктор и
	/// (для классов) не быть абстрактным. Проверяется в <c>GenericInference</c>.
	/// </summary>
	public bool RequiresConstructor { get; }

	/// <summary>Типы аргументов <c>ctor(i32, str)</c>; пусто — достаточно любого конструктора.</summary>
	public IReadOnlyList<KernType> ConstructorParameters { get; }

	/// <summary>
	/// Параметр удовлетворяет ограничению, если оно универсальное числовое
	/// (<c>num</c>/<c>inum</c>/<c>fnum</c>) либо уже указано среди его ограничений.
	/// Структурная проверка появится вместе с Conversions.
	/// </summary>
	public bool Satisfies(KernType constraint) =>
		constraint is PrimitiveType { Primitive: PrimitiveKind.Inum or PrimitiveKind.Fnum or PrimitiveKind.Num } ||
		Constraints.Contains(constraint);

	public bool Equals(TypeParameterType? other) => other is not null && Name == other.Name;

	public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

	public override string ToString() => Name;
}
