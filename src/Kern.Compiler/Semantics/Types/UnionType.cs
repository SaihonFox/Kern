namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Union-тип <c>[A, B]</c>. Создавать только через <see cref="Create"/>: альтернативы
/// раскрываются, дедуплицируются и сортируются по каноническому тексту; одна альтернатива
/// сворачивается в неё саму, ошибка внутри — в <see cref="ErrorType"/>.
/// </summary>
public sealed record UnionType : KernType
{
	private UnionType(IReadOnlyList<KernType> alternatives) : base(TypeKind.Union) => Alternatives = alternatives;

	public IReadOnlyList<KernType> Alternatives { get; }

	public static KernType Create(IReadOnlyList<KernType> alternatives)
	{
		ArgumentNullException.ThrowIfNull(alternatives);
		var flattened = new List<KernType>();
		var hasError = false;
		foreach (var alternative in alternatives)
			Flatten(alternative, flattened, ref hasError);

		if (hasError)
			return ErrorType.Instance;
		if (flattened.Count == 0)
			return ErrorType.Instance;

		var distinct = new List<KernType>(flattened.Count);
		foreach (var alternative in flattened)
			if (!distinct.Contains(alternative))
				distinct.Add(alternative);

		distinct.Sort((left, right) => string.CompareOrdinal(left.ToString(), right.ToString()));
		return distinct.Count == 1 ? distinct[0] : new UnionType(StructuralList<KernType>.From(distinct));
	}

	private static void Flatten(KernType type, List<KernType> target, ref bool hasError)
	{
		switch (type)
		{
			case UnionType union:
				foreach (var alternative in union.Alternatives)
					Flatten(alternative, target, ref hasError);
				break;
			case ErrorType:
				hasError = true;
				break;
			default:
				target.Add(type);
				break;
		}
	}

	public override string ToString() =>
		ResultTypes.TryDescribe(this, out var result)
			? result
			: $"[{string.Join(",", Alternatives)}]";
}
