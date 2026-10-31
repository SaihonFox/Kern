namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Тип значения <c>nil</c> (spec §3, §4). Отдельный от <c>object</c>: неявно преобразуется
/// только в nullable-тип (Q9), поэтому <c>nil</c> нельзя сохранить в <c>object</c>.
/// </summary>
public sealed record NilType : KernType
{
	private NilType() : base(TypeKind.Nil) { }

	public static NilType Instance { get; } = new();

	public override string ToString() => "nil";
}
