using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.CodeGen;

/// <summary>M3-E: codegen match-выражений и проверок типа/структуры паттернов.</summary>
public sealed partial class CodeGenerator
{
	/// <summary>
	/// M4/M3-E: субъект вычисляется один раз во временный слот, затем ветви проверяются
	/// сверху вниз. Проверка паттерна оставляет bool; связывания пишутся в слоты binder'а.
	/// Если ни одна ветвь не совпала, <see cref="OpCode.MatchFail"/> даёт панику (покрытие
	/// диагностируется отдельно, см. Q12).
	/// </summary>
	private void EmitMatch(BoundMatchExpression match) => EmitMatch(match, tail: false);

	private void EmitMatch(BoundMatchExpression match, bool tail)
	{
		int? targetSlot = null;
		if (match.Target is not null)
		{
			targetSlot = AllocateLocalSlot();
			EmitExpression(match.Target);
			EmitCopyIfUnit(match.Target.Type);
			Emit(OpCode.StoreLocal, targetSlot.Value);
		}

		var endJumps = new List<int>();
		foreach (var arm in match.Arms)
		{
			var nextArmJumps = new List<int>();
			EmitMatchArmTest(arm, targetSlot);
			nextArmJumps.Add(EmitJump(OpCode.JmpIfNot));
			if (arm.Guard is not null)
			{
				EmitExpression(arm.Guard);
				nextArmJumps.Add(EmitJump(OpCode.JmpIfNot));
			}

			EmitTailBranch(arm.Body, tail);
			endJumps.Add(EmitJump(OpCode.Jmp));

			foreach (var jump in nextArmJumps)
				Patch(jump, Position);
		}

		Emit(OpCode.MatchFail);
		foreach (var jump in endJumps)
			Patch(jump, Position);
	}

	private void EmitMatchArmTest(BoundMatchArm arm, int? targetSlot)
	{
		if (targetSlot is not { } slot)
		{
			switch (arm.Pattern)
			{
				case BoundWildcardPattern:
					Emit(OpCode.PushTrue);
					return;
				case BoundGuardPattern guard:
					EmitExpression(guard.Condition);
					return;
				default:
					Report(
						"KRN2201",
						$"Паттерн '{arm.Pattern.GetType().Name}' в targetless-match пока не поддержан codegen'ом",
						arm.Pattern.Span);
					Emit(OpCode.PushFalse);
					return;
			}
		}

		Emit(OpCode.LoadLocal, slot);
		EmitPatternTestOnStack(arm.Pattern, arm.Pattern.Span);
	}

	private void EmitIsPattern(BoundIsPatternExpression expression)
	{
		int slot = AllocateLocalSlot();
		EmitExpression(expression.Subject);
		EmitCopyIfUnit(expression.Subject.Type);
		Emit(OpCode.StoreLocal, slot);
		Emit(OpCode.LoadLocal, slot);
		EmitPatternTestOnStack(expression.Pattern, expression.Pattern.Span);
		if (expression.Negated)
			Emit(OpCode.Not);
	}

	/// <summary>Проверка паттерна: субъект лежит на вершине стека, на выходе один bool.</summary>
	private void EmitPatternTestOnStack(BoundPattern pattern, TextSpan span)
	{
		switch (pattern)
		{
			case BoundWildcardPattern:
				Emit(OpCode.Pop);
				Emit(OpCode.PushTrue);
				break;

			case BoundLiteralPattern literal:
				EmitExpression(literal.Value);
				Emit(OpCode.Eq);
				break;

			case BoundRangePattern range:
				{
					// Субъект лежит на вершине; для второй границы сохраняем его во временный слот.
					var rangeSlot = AllocateLocalSlot();
					Emit(OpCode.StoreLocal, rangeSlot);
					Emit(OpCode.LoadLocal, rangeSlot);
					EmitExpression(range.Start);
					Emit(OpCode.Ge);
					Emit(OpCode.LoadLocal, rangeSlot);
					EmitExpression(range.End);
					Emit(range.Inclusive ? OpCode.Le : OpCode.Lt);
					Emit(OpCode.And);
					break;
				}

			case BoundOrPattern or:
				EmitOrPatternTest(or);
				break;

			case BoundListPattern list:
				EmitListPatternTest(list);
				break;

			case BoundTuplePattern tuple:
				EmitTuplePatternTest(tuple);
				break;

			case BoundNilPattern:
				Emit(OpCode.PushNil);
				Emit(OpCode.Eq);
				break;

			case BoundBindingPattern binding:
				EmitCopyIfUnit(binding.Type);
				Emit(OpCode.StoreLocal, binding.Slot);
				Emit(OpCode.PushTrue);
				break;

			case BoundTypePattern type:
				if (type.Binding is BoundBindingPattern typeBinding)
				{
					Emit(OpCode.Dup);
					EmitCopyIfUnit(typeBinding.Type);
					Emit(OpCode.StoreLocal, typeBinding.Slot);
				}
				EmitTypeTestOnStack(type.Type, span);
				break;

			case BoundConstructorPattern constructor:
				EmitCompositePattern(constructor.Type, constructor.Fields, span);
				break;

			case BoundStructuralPattern structural:
				EmitCompositePattern(structural.Type, structural.Fields, span);
				break;

			case BoundGuardPattern guard:
				Emit(OpCode.Pop);
				EmitExpression(guard.Condition);
				break;

			case BoundUnsupportedPattern:
			default:
				Emit(OpCode.Pop);
				Emit(OpCode.PushFalse);
				break;
		}
	}

	/// <summary>
	/// B4a2: or-паттерн. Субъект снимается с вершины стека во временный слот, затем
	/// альтернативы проверяются по очереди: первая успешная даёт <c>true</c>, иначе <c>false</c>.
	/// </summary>
	private void EmitOrPatternTest(BoundOrPattern pattern)
	{
		var subjectSlot = AllocateLocalSlot();
		Emit(OpCode.StoreLocal, subjectSlot);

		var successJumps = new List<int>(pattern.Alternatives.Count);
		foreach (var alternative in pattern.Alternatives)
		{
			Emit(OpCode.LoadLocal, subjectSlot);
			EmitPatternTestOnStack(alternative, alternative.Span);
			successJumps.Add(EmitJump(OpCode.JmpIf));
		}

		Emit(OpCode.PushFalse);
		var endJump = EmitJump(OpCode.Jmp);
		foreach (var jump in successJumps)
			Patch(jump, Position);
		Emit(OpCode.PushTrue);
		Patch(endJump, Position);
	}

	/// <summary>
	/// B4b1/B4b2: паттерн списка. Субъект снимается с вершины стека во временный слот.
	/// Без rest'а длина должна совпасть точно (<c>Eq</c>), с rest'ом — быть не меньше числа
	/// перечисленных элементов (<c>Ge</c>). Элементы до rest'а читаются по абсолютному индексу,
	/// после — от конца: <c>len - k</c>. Именованный rest режется <c>ListSlice</c> в свой слот.
	/// Провал любой проверки переходит на общий <c>false</c>, поэтому за границей длины
	/// элементов не читается.
	/// </summary>
	private void EmitListPatternTest(BoundListPattern pattern)
	{
		var subjectSlot = AllocateLocalSlot();
		Emit(OpCode.StoreLocal, subjectSlot);

		var failJumps = new List<int>();
		Emit(OpCode.LoadLocal, subjectSlot);
		Emit(OpCode.ListLen);
		Emit(OpCode.PushI32, pattern.Items.Count);
		Emit(pattern.HasRest ? OpCode.Ge : OpCode.Eq);
		failJumps.Add(EmitJump(OpCode.JmpIfNot));

		var itemsBefore = pattern.HasRest ? pattern.ItemsBeforeRest : pattern.Items.Count;
		var itemsAfter = pattern.Items.Count - itemsBefore;
		var endSlot = -1;
		if (itemsAfter > 0)
		{
			// Абсолютный индекс элемента после rest'а = длина - смещение от конца.
			endSlot = AllocateLocalSlot();
			Emit(OpCode.LoadLocal, subjectSlot);
			Emit(OpCode.ListLen);
			Emit(OpCode.PushI32, itemsAfter);
			Emit(OpCode.Sub);
			Emit(OpCode.StoreLocal, endSlot);
		}

		for (var index = 0; index < pattern.Items.Count; index++)
		{
			Emit(OpCode.LoadLocal, subjectSlot);
			if (index < itemsBefore)
				Emit(OpCode.PushI32, index);
			else
			{
				Emit(OpCode.LoadLocal, endSlot);
				Emit(OpCode.PushI32, index - itemsBefore);
				Emit(OpCode.Add);
			}
			Emit(OpCode.ListGet);
			EmitPatternTestOnStack(pattern.Items[index], pattern.Items[index].Span);
			failJumps.Add(EmitJump(OpCode.JmpIfNot));
		}

		if (pattern.Rest is { } rest)
		{
			Emit(OpCode.LoadLocal, subjectSlot);
			Emit(OpCode.PushI32, itemsBefore);
			if (itemsAfter > 0)
				Emit(OpCode.LoadLocal, endSlot);
			else
			{
				Emit(OpCode.LoadLocal, subjectSlot);
				Emit(OpCode.ListLen);
			}
			Emit(OpCode.ListSlice);
			Emit(OpCode.StoreLocal, rest.Slot);
		}

		Emit(OpCode.PushTrue);
		var endJump = EmitJump(OpCode.Jmp);
		foreach (var jump in failJumps)
			Patch(jump, Position);
		Emit(OpCode.PushFalse);
		Patch(endJump, Position);
	}

	/// <summary>
	/// Паттерн кортежа: субъект снимается в слот, затем элементы читаются <c>TupleGet</c>
	/// по порядку и проверяются вложенными паттернами. Провал любой проверки — общий <c>false</c>.
	/// </summary>
	private void EmitTuplePatternTest(BoundTuplePattern pattern)
	{
		var subjectSlot = AllocateLocalSlot();
		Emit(OpCode.StoreLocal, subjectSlot);

		var failJumps = new List<int>();
		for (var index = 0; index < pattern.Items.Count; index++)
		{
			Emit(OpCode.LoadLocal, subjectSlot);
			Emit(OpCode.TupleGet, index);
			EmitPatternTestOnStack(pattern.Items[index], pattern.Items[index].Span);
			failJumps.Add(EmitJump(OpCode.JmpIfNot));
		}

		Emit(OpCode.PushTrue);
		var endJump = EmitJump(OpCode.Jmp);
		foreach (var jump in failJumps)
			Patch(jump, Position);
		Emit(OpCode.PushFalse);
		Patch(endJump, Position);
	}

	/// <summary>
	/// Проверка unit/class-паттерна: тест типа, затем по очереди тесты полей. Субъект
	/// остаётся на стеке до общего успеха/провала, чтобы читать из него поля.
	/// </summary>
	private void EmitCompositePattern(KernType type, IReadOnlyList<BoundFieldPattern> fields, TextSpan span)
	{
		Emit(OpCode.Dup);
		EmitTypeTestOnStack(type, span);

		var failJumps = new List<int> { EmitJump(OpCode.JmpIfNot) };
		foreach (var field in fields)
		{
			if (!TryGetFieldSlot(type, field.Name, out var slot))
			{
				Report("KRN2201", $"Поле '{field.Name}' не найдено для паттерна типа '{type}'", field.Span);
				continue;
			}

			Emit(OpCode.Dup);
			Emit(OpCode.LoadField, slot);
			EmitPatternTestOnStack(field.Pattern, field.Span);
			failJumps.Add(EmitJump(OpCode.JmpIfNot));
		}

		Emit(OpCode.Pop);
		Emit(OpCode.PushTrue);
		var endJump = EmitJump(OpCode.Jmp);

		foreach (var jump in failJumps)
			Patch(jump, Position);
		Emit(OpCode.Pop);
		Emit(OpCode.PushFalse);
		Patch(endJump, Position);
	}

	/// <summary>
	/// Тест «значение на вершине имеет тип <paramref name="type"/>». Union проверяется
	/// по альтернативам, nullable — как nil или underlying. Точные типы — опкодом IsType.
	/// </summary>
	private void EmitTypeTestOnStack(KernType type, TextSpan span)
	{
		switch (type)
		{
			case UnionType union:
				{
					var slot = AllocateLocalSlot();
					Emit(OpCode.StoreLocal, slot);
					if (union.Alternatives.Count == 0)
					{
						Emit(OpCode.PushFalse);
						return;
					}
					for (var index = 0; index < union.Alternatives.Count; index++)
					{
						Emit(OpCode.LoadLocal, slot);
						EmitTypeTestOnStack(union.Alternatives[index], span);
						if (index > 0)
							Emit(OpCode.Or);
					}
					return;
				}

			case NullableType nullable:
				{
					var slot = AllocateLocalSlot();
					Emit(OpCode.StoreLocal, slot);
					Emit(OpCode.LoadLocal, slot);
					Emit(OpCode.PushNil);
					Emit(OpCode.Eq);
					Emit(OpCode.LoadLocal, slot);
					EmitTypeTestOnStack(nullable.Underlying, span);
					Emit(OpCode.Or);
					return;
				}

			default:
				if (!HasExactTypeSig(type))
				{
					Report("KRN2201", $"Проверка типа '{type}' в match пока не поддержана codegen'ом", span);
					Emit(OpCode.Pop);
					Emit(OpCode.PushFalse);
					return;
				}
				Emit(OpCode.IsType, _module.InternTypeSig(TypeSigMap.From(type, _module.Name)));
				return;
		}
	}

	/// <summary>Есть ли у типа точная подпись в .kbc (иначе TypeSigMap дал бы object).</summary>
	private static bool HasExactTypeSig(KernType type) => type switch
	{
		PrimitiveType => true,
		UnitType unit => unit.Arguments.All(HasExactTypeSig),
		ClassType @class => @class.Arguments.All(HasExactTypeSig),
		BuiltinGenericType builtin => builtin.Arguments.All(HasExactTypeSig),
		_ => false
	};

	private bool TryGetFieldSlot(KernType type, string name, out int slot)
	{
		var symbol = type switch
		{
			UnitType unit => unit.Symbol,
			ClassType @class => @class.Symbol,
			_ => null
		};
		if (symbol?.Members.FindField(name) is { } field && _fieldSlots.TryGetValue(field, out slot))
			return true;

		slot = -1;
		return false;
	}

	private int AllocateLocalSlot()
	{
		var slot = _current.LocalCount;
		_current.LocalCount++;
		return slot;
	}
}
