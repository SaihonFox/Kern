using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.CodeGen;

/// <summary>
/// Запись через цепь членов (M3-D4, Q13). Каждое промежуточное <c>unit</c>-значение
/// копируется (<c>CopyUnit</c>), изменяется и записывается обратно наружу; для
/// class-ссылок копии и обратной записи нет. Рецепт для <c>o.inner.x = v</c>:
/// <c>load o · CopyUnit · Dup · Dup · LoadField inner · CopyUnit · Dup · v ·
/// StoreField x · Pop · StoreField inner · Pop · StoreLocal o</c>. Каждый уровень
/// возвращает объемлющее значение наверх, поэтому стек после оператора — пуст.
/// </summary>
public sealed partial class CodeGenerator
{
	private void EmitChainStore(BoundChainStore store)
	{
		EmitStoreRootLoad(store.Root);
		var rootIsUnit = store.Root.Type is UnitType;
		if (rootIsUnit)
			Emit(OpCode.CopyUnit);

		// Защита от рассинхронизации с Binder'ом: без звеньев нечего писать.
		if (store.Links.Count == 0)
		{
			Report("KRN2201", "Цепь записи без звеньев (M3-D4)", store.Span);
			Emit(OpCode.Pop);
			return;
		}

		EmitStoreLinks(store.Links, 0, store.Value);

		if (rootIsUnit)
			EmitStoreRootWriteBack(store.Root);
		else
			Emit(OpCode.Pop);
	}

	/// <summary>
	/// Рекурсивно спускается по звеньям. На входе на стеке объемлющее значение,
	/// на выходе — оно же, изменённое (нетто 0): unit-звено копируется, пишется
	/// обратно в объемлющее значение и оставляет его на стеке, class-звено
	/// изменяется на месте и снимается со стека.
	/// </summary>
	private void EmitStoreLinks(IReadOnlyList<BoundStoreLink> links, int index, BoundExpression value)
	{
		var link = links[index];
		if (index == links.Count - 1)
		{
			// Приёмник остаётся на стеке: по нему объемлющий уровень пишет
			// изменённую копию звена обратно.
			Emit(OpCode.Dup);
			EmitExpression(value);
			EmitCopyIfUnit(link.Type);
			EmitStoreLink(link);
			Emit(OpCode.Pop);
			return;
		}

		// Для unit-звена нужны ещё две копии объемлющего значения: одна станет
		// приёмником LoadLink, вторая — приёмником обратной записи звена; третья
		// (нижняя) вернётся наверх для записи в родителя.
		if (link.Type is UnitType)
			Emit(OpCode.Dup);
		Emit(OpCode.Dup);
		EmitLoadLink(link);
		if (link.Type is UnitType)
			Emit(OpCode.CopyUnit);

		EmitStoreLinks(links, index + 1, value);

		if (link.Type is UnitType)
			EmitStoreLink(link);
		Emit(OpCode.Pop);
	}

	private void EmitStoreRootLoad(BoundStoreRoot root)
	{
		switch (root)
		{
			case BoundLocalStoreRoot local:
				Emit(OpCode.LoadLocal, local.Slot);
				break;

			case BoundSelfStoreRoot self:
				Emit(OpCode.LoadLocal, self.Slot);
				break;

			case BoundStaticFieldStoreRoot field:
				if (_staticSlots.TryGetValue(field.Field, out var staticSlot))
					Emit(OpCode.LoadStatic, staticSlot);
				else
				{
					Report("KRN2201", $"Статическое поле '{field.Field.Name}' не попало в FieldPool (M3-D4)", field.Span);
					Emit(OpCode.PushVoid);
				}
				break;

			case BoundStaticPropertyStoreRoot property:
				EmitCallTo(property.Getter, argumentCount: 0, property.Span);
				break;

			case BoundExpressionStoreRoot expression:
				EmitExpression(expression.Expression);
				break;

			default:
				Report("KRN2201", "Неизвестный корень цепи записи (M3-D4)", root.Span);
				Emit(OpCode.PushVoid);
				break;
		}
	}

	/// <summary>
	/// Обратная запись скопированного unit-корня; снимает значение со стека и
	/// ничего не кладёт (netto −1). <c>StoreLocal</c> забирает значение сам,
	/// <c>StoreStatic</c> и setter кладут <c>Void</c> — его нужно снять.
	/// </summary>
	private void EmitStoreRootWriteBack(BoundStoreRoot root)
	{
		switch (root)
		{
			case BoundLocalStoreRoot local:
				Emit(OpCode.StoreLocal, local.Slot);
				return;

			case BoundSelfStoreRoot self:
				Emit(OpCode.StoreLocal, self.Slot);
				return;

			case BoundStaticFieldStoreRoot field:
				if (_staticSlots.TryGetValue(field.Field, out var staticSlot))
					Emit(OpCode.StoreStatic, staticSlot);
				else
					Report("KRN2201", $"Статическое поле '{field.Field.Name}' не попало в FieldPool (M3-D4)", field.Span);
				Emit(OpCode.Pop);
				return;

			case BoundStaticPropertyStoreRoot property:
				EmitCallTo(property.Setter, argumentCount: 1, property.Span);
				Emit(OpCode.Pop);
				return;

			default:
				Report("KRN2201", "Временное unit-значение нельзя записать обратно (M3-D4)", root.Span);
				Emit(OpCode.Pop);
				return;
		}
	}

	/// <summary>Читает звено: receiver со стека заменяется значением звена.</summary>
	private void EmitLoadLink(BoundStoreLink link)
	{
		switch (link)
		{
			case BoundFieldStoreLink field:
				if (_fieldSlots.TryGetValue(field.Field, out var fieldSlot))
					Emit(OpCode.LoadField, fieldSlot);
				else
				{
					Report("KRN2201", $"Поле '{field.Field.Name}' не попало в TypeDef (M3-D4)", field.Span);
					Emit(OpCode.Pop);
					Emit(OpCode.PushVoid);
				}
				break;

			case BoundPropertyStoreLink { Getter: { } getter } property:
				EmitCallTo(getter, argumentCount: 1, property.Span);
				break;

			default:
				Report("KRN2201", "Звено цепи записи недоступно для чтения (M3-D4)", link.Span);
				Emit(OpCode.Pop);
				Emit(OpCode.PushVoid);
				break;
		}
	}

	/// <summary>Пишет значение звена обратно в receiver; consumes receiver + value, leaves Void.</summary>
	private void EmitStoreLink(BoundStoreLink link)
	{
		switch (link)
		{
			case BoundFieldStoreLink field:
				if (_fieldSlots.TryGetValue(field.Field, out var fieldSlot))
					Emit(OpCode.StoreField, fieldSlot);
				else
				{
					Report("KRN2201", $"Поле '{field.Field.Name}' не попало в TypeDef (M3-D4)", field.Span);
					Emit(OpCode.Pop);
					Emit(OpCode.Pop);
					Emit(OpCode.PushVoid);
				}
				break;

			case BoundPropertyStoreLink { Writer: { } writer } property:
				EmitCallTo(writer, argumentCount: 2, property.Span);
				break;

			default:
				Report("KRN2201", "Звено цепи записи недоступно для записи (M3-D4)", link.Span);
				Emit(OpCode.Pop);
				Emit(OpCode.Pop);
				Emit(OpCode.PushVoid);
				break;
		}
	}

	/// <summary>
	/// Текущее значение конечного звена для составного присваивания: приёмник уже
	/// на стеке, поэтому он дублируется перед чтением.
	/// </summary>
	private void EmitStoreCurrent(BoundStoreCurrentExpression expression)
	{
		Emit(OpCode.Dup);
		EmitLoadLink(expression.Link);
	}

	/// <summary>Вызов уже подготовленных аргументов (приёмник входит в <paramref name="argumentCount"/>).</summary>
	private void EmitCallTo(FunctionSymbol function, int argumentCount, TextSpan span, bool isVirtual = false)
	{
		if (!_functionIndices.TryGetValue(function, out var index))
		{
			Report("KRN2201", $"Функция '{function.Name}' не имеет байткода (M3-D4)", span);
			for (var argument = 0; argument < argumentCount; argument++)
				Emit(OpCode.Pop);
			Emit(OpCode.PushVoid);
			return;
		}

		Emit(isVirtual ? OpCode.CallVirtual : OpCode.Call);
		EmitOperand(index);
		EmitOperand(argumentCount);
	}
}
