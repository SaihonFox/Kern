using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding;

public sealed partial class Binder
{
	/// <summary>
	/// Присваивание свойству без backing-поля опускается до вызова setter'а.
	/// Auto-свойства обрабатываются как поле (<see cref="PropertySymbol.BackingField"/>).
	/// Нативные сеттеры (`b.Capacity = …`) ищутся среди <see cref="NativeMembers"/>.
	/// </summary>
	private BoundStatement? TryBindPropertyAssignment(AssignmentStatement assignment, MemberAccessExpression member)
	{
		if (assignment.Operator != "=")
			return null;

		var receiver = member.Target is SelfExpression self
			? BindSelf(self)
			: BindExpression(member.Target);
		if (IsError(receiver.Type))
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);

		var owner = OwnerOf(receiver.Type);
		var property = owner?.Members.FindProperty(member.Member);
		if (property is null)
		{
			foreach (FunctionSymbol native in NativeMembers(receiver.Type, member.Member))
			{
				if (native.Kind != FunctionKind.Setter || native.Parameters.Count != 1)
					continue;
				KernType valueType = native.Parameters[0].Type;
				BoundExpression nativeValue = Convert(
					BindExpression(assignment.Value, valueType), valueType, assignment.Value.Span);
				if (nativeValue.Type is ErrorType)
					return new BoundBlock([], null, FallsThrough: true, assignment.Span);
				return new BoundExpressionStatement(
					new BoundCallExpression(native, [nativeValue], CallResultType(native, native.ReturnType),
						assignment.Span, receiver),
					assignment.Span);
			}
			return null;
		}

		// Статическое свойство через экземпляр — неверное направление (M3-D3b).
		if (property.IsStatic)
		{
			Report(
				"KRN2103",
				$"Статический член '{property.Name}' нельзя изменять через экземпляр; нужен тип '{owner!.Name}'",
				assignment.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		// Запись в свойство временного unit-значения ушла бы в копию (Q13/M3-D4).
		if (receiver.Type is UnitType { IsImmutable: false } && !IsPlaceReceiver(receiver))
		{
			Report(
				"KRN2106",
				$"Нельзя изменить свойство '{property.Name}' у временного unit-значения: изменение будет потеряно",
				assignment.Target.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		// init-свойство без backing-поля пишется вызовом accessor'а; правила контекста —
		// в BindInitAccessorAssignment (Q15b). Авто-свойство идёт ниже как обычное поле.
		if (property is { BackingField: null, Setter: null, Init: { Body: not null } init })
			return BindInitAccessorAssignment(assignment, receiver, property, init);

		if (property is not { BackingField: null, Setter: { } setter })
			return null;

		if (owner is { IsImmutable: true })
			Report("KRN2105", $"Свойство '{property.Name}' нельзя изменять у immutable unit!-типа", assignment.Span);

		var value = BindExpression(assignment.Value, property.Type);
		value = Convert(value, property.Type, assignment.Value.Span);
		var call = new BoundCallExpression(setter, [value], property.Type, assignment.Span, receiver)
		{
			// Q30: accessor'ы класса диспатчатся по типу получателя, как и методы.
			// init-запись (Binder.InitWrites.cs) остаётся статической: это конструкция объекта.
			Virtual = IsVirtualCall(receiver, setter),
		};
		return new BoundExpressionStatement(call, assignment.Span);
	}
}
