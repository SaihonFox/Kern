using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Поиск экземплярных членов по цепочке наследования (slice A1). Собственный член с тем же
/// именем важнее унаследованного; статические члены родителя НЕ наследуются (spec §8), поэтому
/// звенья, где найден только статический член, пропускаются при поиске унаследованного.
/// </summary>
public sealed partial class Binder
{
	/// <summary>
	/// Получатели для поиска члена: сам тип, затем родители с подставленными аргументами.
	/// У <c>unit</c> родителей нет — остаётся одно звено.
	/// </summary>
	private static IEnumerable<(TypeSymbol Symbol, KernType Type)> InstanceMemberOwners(KernType receiver)
	{
		switch (receiver)
		{
			case ClassType @class:
				foreach (var link in Inheritance.Chain(@class))
					yield return (link.Symbol, link);
				break;

			case UnitType unit:
				yield return (unit.Symbol, unit);
				break;
		}
	}

	/// <summary>
	/// Тип унаследованного экземплярного члена с подставленными аргументами получателя:
	/// у <c>Derived&lt;i32&gt; [Base&lt;T&gt;]</c> поле <c>T value</c> читается как <c>i32</c>.
	/// </summary>
	private static KernType MemberType(KernType declared, KernType ownerType) =>
		ownerType is ClassType @class && @class.Arguments.Count > 0
			? TypeSubstitution.Apply(declared, Inheritance.ArgumentsMap(@class))
			: declared;

	/// <summary>Собственное звено цепочки (для диагностик «нужен экземпляр/тип»).</summary>
	private static bool IsOwnOwner(KernType receiver, TypeSymbol owner) =>
		OwnerOf(receiver) is { } self && ReferenceEquals(self, owner);
}
