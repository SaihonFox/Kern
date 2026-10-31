namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Разрешённый тип. Records дают структурное равенство; вложенные списки типов хранятся
/// в <see cref="StructuralList{T}"/>, чтобы равенство не зависело от ссылки на массив.
/// </summary>
public abstract record KernType(TypeKind Kind)
{
	/// <summary>Канонический текст типа: <c>list&lt;i32&gt;</c>, <c>i32?</c>, <c>fun(i32)-&gt;str</c>.</summary>
	public abstract override string ToString();
}
