namespace Kern.Binding.Attributes;

public enum KernConstraintKind { Num, Inum, Fnum, Hashable, Comparable }

/// <summary>
/// Kern-ограничение на generic-параметр C#-метода БЕЗ ограничения в C#:
/// <c>static T Abs&lt;[KernConstraint(KernConstraintKind.Num)] T&gt;(T self)</c>.
/// Предпочтительнее <c>where T : IKernNum</c>, т.к. T остаётся чистым <c>KValue</c> (KValue не реализует маркеры).
/// </summary>
[AttributeUsage(AttributeTargets.GenericParameter, AllowMultiple = true)]
public sealed class KernConstraintAttribute(KernConstraintKind kind) : Attribute
{
	public KernConstraintKind Kind { get; } = kind;
}
