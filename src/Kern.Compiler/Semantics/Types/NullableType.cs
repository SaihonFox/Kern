namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Nullable-обёртка <c>T?</c>. Создавать только через <see cref="Create"/>:
/// nullable от nullable — сам nullable, nullable от <see cref="ErrorType"/> — ошибка.
/// </summary>
public sealed record NullableType : KernType
{
	private NullableType(KernType underlying) : base(TypeKind.Nullable) => Underlying = underlying;

	public KernType Underlying { get; }

	public static KernType Create(KernType underlying)
	{
		ArgumentNullException.ThrowIfNull(underlying);
		return underlying switch
		{
			ErrorType => ErrorType.Instance,
			NullableType nested => nested,
			_ => new NullableType(underlying)
		};
	}

	public override string ToString() => $"{Underlying}?";
}
