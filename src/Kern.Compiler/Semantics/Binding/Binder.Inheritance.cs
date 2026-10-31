using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

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

	/// <summary>
	/// Поле/свойство своего экземпляра без <c>$</c> (spec §8): <c>radius</c>, <c>_val</c>,
	/// <c>items</c> внутри тела типа. Локальные имена проверяются вызывающим кодом раньше.
	/// </summary>
	private BoundExpression? TryBindImplicitMemberRead(string name, TextSpan span)
	{
		if (_selfVariable is null || _selfType is null || OwnerOf(_selfType) is not { } owner)
			return null;
		if (owner.Members.FindField(name) is null && owner.Members.FindProperty(name) is null &&
			owner.Members.MemberConflictOwners(name).Count == 0)
			return null;

		return BindMemberAccessOn(AccessVariable(_selfVariable, span), ImplicitMember(name, span));
	}

	/// <summary>Вызов метода своего экземпляра без <c>$</c>: <c>hello()</c>, <c>area()</c>.</summary>
	private BoundExpression? TryBindImplicitMemberCall(
		string name,
		IReadOnlyList<CallArgumentNode> arguments,
		IReadOnlyList<TypeSyntax> typeArguments,
		TextSpan span)
	{
		if (_selfVariable is null || _selfType is null || OwnerOf(_selfType) is not { } owner)
			return null;
		if (!owner.Members.GetFunctions(name).Any(function => !function.IsStatic))
			return null;

		return BindMemberCallOn(
			AccessVariable(_selfVariable, span),
			ImplicitMember(name, span),
			arguments,
			typeArguments,
			span);
	}

	/// <summary>
	/// Нужен ли динамический диспатч (spec §8): метод экземпляра класса, у которого есть
	/// собственный объявленный владелец-класс. Статические и нативные члены, расширения,
	/// методы <c>unit</c>/<c>unit!</c> и конструкторы остаются обычным <c>Call</c>.
	/// </summary>
	private static bool IsVirtualCall(KernType receiverType, FunctionSymbol function) =>
		receiverType is ClassType &&
		function is
		{
			IsStatic: false,
			IsNative: false,
			IsExtension: false,
			Owner: { IsValueType: false, Kind: not Types.SymbolKind.Native },
		} &&
		function.Kind != FunctionKind.Constructor;

	/// <summary>Запись в поле/свойство своего экземпляра без <c>$</c>: <c>_val = value * 2</c>.</summary>
	private BoundStatement? TryBindImplicitMemberStore(AssignmentStatement assignment, string name, TextSpan span)
	{
		if (_selfVariable is null || _selfType is null || OwnerOf(_selfType) is not { } owner)
			return null;
		if (owner.Members.FindField(name) is null && owner.Members.FindProperty(name) is null &&
			owner.Members.MemberConflictOwners(name).Count == 0)
			return null;

		return BindFieldAssignment(assignment, ImplicitMember(name, span));
	}

	/// <summary>
	/// Неоднозначный член при двух и более родителях: одна ошибка KRN2103 на использование.
	/// <c>true</c> — конфликт найден, диагностика выдана; вызывающий возвращает ошибку.
	/// </summary>
	private bool ReportMemberConflict(TypeSymbol owner, string name, TextSpan span)
	{
		var owners = owner.Members.MemberConflictOwners(name);
		if (owners.Count == 0)
			return false;

		var list = owners.Count == 2
			? $"'{owners[0]}' и '{owners[1]}'"
			: string.Join(", ", owners.Select(item => $"'{item}'"));
		Report(
			"KRN2103",
			$"Неоднозначный член '{name}': он объявлен в {list}; выберите родителя, например '$[{owners[0]}].{name}'",
			span);
		return true;
	}

	private static MemberAccessExpression ImplicitMember(string name, TextSpan span) =>
		new(new SelfExpression(span), name, IsConditional: false, span);
}
