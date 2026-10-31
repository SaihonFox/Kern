using Kern.Compiler.Lexing;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Литералы коллекций (spec §3): неявный <c>[1, 2, 3]</c> → <c>list&lt;i32&gt;</c>,
/// <c>[..xs, 4]</c> — spread на своём месте, <c>[[1], [2, 3]]</c> → <c>list&lt;list&lt;i32&gt;&gt;</c>.
/// Явный тип элемента (<c>&lt;i32&gt;[...]</c>) фиксирует <c>T</c>, а вид результата выбирает
/// подсказка: <c>array&lt;T&gt;</c> даёт array-литерал, иначе — <c>list&lt;T&gt;</c>.
/// Словари: <c>[k: v]</c> выводит <c>dict&lt;K, V&gt;</c> из пар, <c>&lt;K: V&gt;[k: v]</c>
/// фиксирует типы ключа и значения; пустой <c>&lt;K: V&gt;[]</c> допустим.
/// </summary>
public sealed partial class Binder
{
	private BoundExpression BindCollectionLiteral(CollectionExpression literal, KernType? hint)
	{
		if (literal.Items.Count == 0)
		{
			// Q21: при известном ожидаемом типе пустой литерал допустим.
			if (hint is BuiltinGenericType { BuiltinKind: BuiltinGenericKind.Dict } dictionary)
				return new BoundDictionaryLiteral([], dictionary, literal.Span);
			if (hint is BuiltinGenericType { BuiltinKind: BuiltinGenericKind.List or BuiltinGenericKind.Array } collection)
				return new BoundCollectionLiteral([], collection, literal.Span);

			// spec §3: без ожидаемого типа вид и T не выводятся.
			Report(
				"KRN2102",
				"Нельзя вывести тип пустого списка '[]': укажите тип элементов явно, например '<i32>[]'",
				literal.Span);
			return new BoundErrorExpression(literal.Span);
		}

		var elementHint = ExpectedElementType(hint);
		var items = new List<BoundCollectionItem>(literal.Items.Count);
		var candidates = new List<KernType>(literal.Items.Count);
		foreach (ExpressionNode item in literal.Items)
		{
			if (item is ListSpreadExpression spread)
			{
				var value = BindExpression(spread.Expression);
				if (IsError(value.Type))
					return new BoundErrorExpression(literal.Span);
				if (CollectionElementType(value.Type) is not { } spreadElementType)
				{
					Report("KRN2101", $"Spread '..' требует 'list' или 'array', получено '{value.Type}'", spread.Span);
					return new BoundErrorExpression(literal.Span);
				}

				items.Add(new BoundCollectionItem(value, IsSpread: true));
				candidates.Add(spreadElementType);
				continue;
			}

			// Подсказка ожидаемого типа проходит в вложенные литералы: list<list<i64>> m = [[1]].
			var element = BindExpression(item, elementHint);
			if (IsError(element.Type))
				return new BoundErrorExpression(literal.Span);

			items.Add(new BoundCollectionItem(element, IsSpread: false));
			candidates.Add(element.Type);
		}

		var elementType = elementHint is not null && HintFits(items, elementHint)
			? elementHint
			: UnifyElementTypes(candidates, literal.Span);
		if (elementType is null)
			return new BoundErrorExpression(literal.Span);

		var converted = new List<BoundCollectionItem>(items.Count);
		foreach (BoundCollectionItem item in items)
		{
			converted.Add(item.IsSpread
				? item
				: new BoundCollectionItem(Convert(item.Value, elementType, item.Value.Span), IsSpread: false));
		}

		var type = new BuiltinGenericType(BuiltinGenericKind.List, [elementType]);
		return CollectionHintMismatch(type, hint, literal.Span)
			?? new BoundCollectionLiteral(converted, type, literal.Span);
	}

	/// <summary>
	/// Литерал с явным типом элемента (spec §3): <c>&lt;i32&gt;[1, 2, 3]</c>. Элементы
	/// связываются с подсказкой <c>T</c> и приводятся к ней (числовое расширение — как у
	/// неявного литерала), spread обязан иметь ровно <c>list&lt;T&gt;</c>/<c>array&lt;T&gt;</c>.
	/// Вид результата выбирает ожидаемый тип: только точный <c>array&lt;T&gt;</c> даёт
	/// array-литерал, любой другой (или отсутствие подсказки) — <c>list&lt;T&gt;</c>.
	/// </summary>
	private BoundExpression BindTypedCollectionLiteral(TypedCollectionExpression literal, KernType? hint)
	{
		// <K: V>[...] — словарный литерал: типы ключа и значения заданы явно.
		if (literal.KeyType is not null)
			return BindTypedDictionaryLiteral(literal, hint);

		// Пара «ключ: значение» в списковом литерале невозможна: пары бывают только в словаре.
		if (literal.ElementType is not { } elementSyntax)
		{
			Report("KRN2101", "Литерал коллекции должен иметь тип элемента, например '<i32>[...]'", literal.Span);
			return new BoundErrorExpression(literal.Span);
		}

		if (literal.Entries.Count > 0)
		{
			Report(
				"KRN2101",
				"Пара 'ключ: значение' допустима только в литерале словаря '<K: V>[...]'",
				literal.Span);
			return new BoundErrorExpression(literal.Span);
		}

		var elementType = _resolver.Resolve(elementSyntax, _typeScope);
		if (IsError(elementType))
			return new BoundErrorExpression(literal.Span);

		var isArray = hint is BuiltinGenericType
		{
			BuiltinKind: BuiltinGenericKind.Array,
			Arguments.Count: 1
		} array && array.Arguments[0].Equals(elementType);

		var items = new List<BoundCollectionItem>(literal.Items.Count);
		foreach (ExpressionNode item in literal.Items)
		{
			if (item is ListSpreadExpression spread)
			{
				// NewArray не умеет добавлять после создания, а ListAppendRange требует
				// list-приёмник: в array-литерале spread не поддержан.
				if (isArray)
				{
					Report("KRN2101", "Spread '..' не поддержан для array-литерала", spread.Span);
					return new BoundErrorExpression(literal.Span);
				}

				var value = BindExpression(spread.Expression);
				if (IsError(value.Type))
					return new BoundErrorExpression(literal.Span);
				if (CollectionElementType(value.Type) is not { } spreadElement)
				{
					Report("KRN2101", $"Spread '..' требует 'list' или 'array', получено '{value.Type}'", spread.Span);
					return new BoundErrorExpression(literal.Span);
				}
				if (!spreadElement.Equals(elementType))
				{
					Report(
						"KRN2101",
						$"Spread '..' должен иметь элементы типа '{elementType}', получено '{spreadElement}'",
						spread.Span);
					return new BoundErrorExpression(literal.Span);
				}

				items.Add(new BoundCollectionItem(value, IsSpread: true));
				continue;
			}

			var element = BindExpression(item, elementType);
			if (IsError(element.Type))
				return new BoundErrorExpression(literal.Span);
			var converted = Convert(element, elementType, element.Span);
			if (IsError(converted.Type))
				return new BoundErrorExpression(literal.Span);
			items.Add(new BoundCollectionItem(converted, IsSpread: false));
		}

		var type = new BuiltinGenericType(
			isArray ? BuiltinGenericKind.Array : BuiltinGenericKind.List,
			[elementType]);
		return CollectionHintMismatch(type, hint, literal.Span)
			?? new BoundCollectionLiteral(items, type, literal.Span);
	}

	/// <summary>
	/// Неявный литерал словаря (spec §3): <c>["k": 1, "j": 2]</c>. Ключи объединяются в один
	/// <c>K</c>, значения — в один <c>V</c> по тому же правилу, что элементы списка (числовое
	/// расширение только внутри семейства, прочие типы совпадают точно). Ожидаемый
	/// <c>dict&lt;K, V&gt;</c> — подсказка: числа в паре расширяются до <c>K</c>/<c>V</c>,
	/// иначе берётся выведенный тип и несовпадение с ожидаемым — KRN2101.
	/// </summary>
	private BoundExpression BindDictionaryLiteral(DictionaryExpression literal, KernType? hint)
	{
		var (keyHint, valueHint) = ExpectedDictionaryTypes(hint);
		var entries = new List<BoundCollectionEntry>(literal.Entries.Count);
		var keyCandidates = new List<KernType>(literal.Entries.Count);
		var valueCandidates = new List<KernType>(literal.Entries.Count);
		foreach (CollectionEntryNode entry in literal.Entries)
		{
			// Подсказка проходит в ключ и значение: вложенные литералы выводятся из K/V.
			var key = BindExpression(entry.Key, keyHint);
			if (IsError(key.Type))
				return new BoundErrorExpression(literal.Span);
			var value = BindExpression(entry.Value, valueHint);
			if (IsError(value.Type))
				return new BoundErrorExpression(literal.Span);

			entries.Add(new BoundCollectionEntry(key, value));
			keyCandidates.Add(key.Type);
			valueCandidates.Add(value.Type);
		}

		if (entries.Count == 0)
		{
			// Как и пустой [], литерал без пар не выводит K и V (spec §3).
			Report(
				"KRN2102",
				"Нельзя вывести тип пустого словаря '[]': укажите типы явно, например '<str: i32>[]'",
				literal.Span);
			return new BoundErrorExpression(literal.Span);
		}

		var keyType = keyHint is not null && HintFitsTypes(keyCandidates, keyHint)
			? keyHint
			: UnifyElementTypes(keyCandidates, literal.Span, "ключей словаря");
		if (keyType is null)
			return new BoundErrorExpression(literal.Span);

		// Ключ словаря обязан быть хэшируемым (spec §3, §9): проверяем до приведения пар,
		// чтобы не выдавать вторичных диагностик о значениях.
		if (!IsHashableKey(keyType))
		{
			Report("KRN2101", HashabilityMessage(keyType), literal.Span);
			return new BoundErrorExpression(literal.Span);
		}

		var valueType = valueHint is not null && HintFitsTypes(valueCandidates, valueHint)
			? valueHint
			: UnifyElementTypes(valueCandidates, literal.Span, "значений словаря");
		if (valueType is null)
			return new BoundErrorExpression(literal.Span);

		var converted = new List<BoundCollectionEntry>(entries.Count);
		foreach (BoundCollectionEntry entry in entries)
		{
			var key = Convert(entry.Key, keyType, entry.Key.Span);
			if (IsError(key.Type))
				return new BoundErrorExpression(literal.Span);
			var value = Convert(entry.Value, valueType, entry.Value.Span);
			if (IsError(value.Type))
				return new BoundErrorExpression(literal.Span);
			converted.Add(new BoundCollectionEntry(key, value));
		}

		var type = new BuiltinGenericType(BuiltinGenericKind.Dict, [keyType, valueType]);
		return CollectionHintMismatch(type, hint, literal.Span)
			?? new BoundDictionaryLiteral(converted, type, literal.Span);
	}

	/// <summary>
	/// Литерал словаря с явными типами (spec §3): <c>&lt;str: i32&gt;["a": 1]</c>. Ключ и
	/// значение связываются с подсказкой <c>K</c>/<c>V</c> и приводятся к ним (числовое
	/// расширение разрешено); <c>&lt;K: V&gt;[]</c> — пустой словарь.
	/// </summary>
	private BoundExpression BindTypedDictionaryLiteral(TypedCollectionExpression literal, KernType? hint)
	{
		var keyType = _resolver.Resolve(literal.KeyType!, _typeScope);
		var valueType = literal.ValueType is null
			? ErrorType.Instance
			: _resolver.Resolve(literal.ValueType, _typeScope);
		if (IsError(keyType) || IsError(valueType))
			return new BoundErrorExpression(literal.Span);

		// В словарном литерале каждый элемент обязан быть парой «ключ: значение».
		if (literal.Items.Count > 0)
		{
			Report(
				"KRN2101",
				"Элемент литерала словаря должен быть парой 'ключ: значение'",
				literal.Span);
			return new BoundErrorExpression(literal.Span);
		}

		// Явно заданный ключ проверяется тем же правилом, что и выведенный (spec §3, §9).
		if (!IsHashableKey(keyType))
		{
			Report("KRN2101", HashabilityMessage(keyType), literal.Span);
			return new BoundErrorExpression(literal.Span);
		}

		var entries = new List<BoundCollectionEntry>(literal.Entries.Count);
		foreach (CollectionEntryNode entry in literal.Entries)
		{
			var key = Convert(BindExpression(entry.Key, keyType), keyType, entry.Key.Span);
			if (IsError(key.Type))
				return new BoundErrorExpression(literal.Span);
			var value = Convert(BindExpression(entry.Value, valueType), valueType, entry.Value.Span);
			if (IsError(value.Type))
				return new BoundErrorExpression(literal.Span);
			entries.Add(new BoundCollectionEntry(key, value));
		}

		var type = new BuiltinGenericType(BuiltinGenericKind.Dict, [keyType, valueType]);
		return CollectionHintMismatch(type, hint, literal.Span)
			?? new BoundDictionaryLiteral(entries, type, literal.Span);
	}

	/// <summary>
	/// Хэшируем ли тип в роли ключа <c>dict</c> (spec §3, §9; Q23 отложен). Хэшируемы:
	/// числа (включая <c>inum</c>/<c>fnum</c>/<c>num</c>), <c>bool</c>, <c>char</c>, <c>str</c>,
	/// класс (идентичность ссылки), параметр обобщения (неизвестен — считаем да),
	/// <c>T?</c> при хэшируемом <c>T</c> и <c>unit</c>/<c>unit!</c>, у которого все
	/// экземплярные поля хэшируемы. Коллекции, кортежи, функции, union, <c>object</c>,
	/// <c>void</c>, <c>never</c> и <c>nil</c> — нет.
	/// </summary>
	private static bool IsHashableKey(KernType type) => IsHashableKey(type, []);

	private static bool IsHashableKey(KernType type, HashSet<TypeSymbol> visiting) => type switch
	{
		PrimitiveType primitive => NumericInfo.TryGet(primitive.Primitive, out _) ||
			primitive.Primitive is PrimitiveKind.Bool or PrimitiveKind.Char or PrimitiveKind.Str,
		// Класс сравнивается и хэшируется по идентичности ссылки (spec §9, решение §4).
		ClassType => true,
		// Конкретный аргумент неизвестен: не мешаем вызову и считаем хэшируемым.
		TypeParameterType => true,
		// T? хэшируется как T: nil — отдельное значение со своим хэшем.
		NullableType nullable => IsHashableKey(nullable.Underlying, visiting),
		// Ошибка уже диагностирована — не порождаем каскад.
		ErrorType => true,
		UnitType unit => IsHashableUnit(unit, visiting),
		TupleType tuple => tuple.Elements.All(element => IsHashableKey(element.Type, visiting)),
		_ => false,
	};

	/// <summary>
	/// Экземплярные поля unit'а должны быть хэшируемы рекурсивно (spec §9). Рекурсивный
	/// unit — поле того же типа — считается хэшируемым: цикл не делает структурный хэш
	/// невозможным.
	/// </summary>
	private static bool IsHashableUnit(UnitType unit, HashSet<TypeSymbol> visiting)
	{
		if (!visiting.Add(unit.Symbol))
			return true;

		foreach (FieldSymbol field in unit.Symbol.Members.Fields)
		{
			if (field.IsStatic || field.IsBackingField)
				continue;
			if (!IsHashableKey(field.Type, visiting))
				return false;
		}

		return true;
	}

	/// <summary>Диагностика нехэшируемого ключа словаря: одна формулировка на оба литерала.</summary>
	private static string HashabilityMessage(KernType keyType) =>
		$"Тип ключа '{keyType}' не хэшируем: нужен примитив, str, класс или unit с хэшируемыми полями";

	/// <summary>
	/// Литерал собран как <c>list&lt;T&gt;</c>/<c>array&lt;T&gt;</c>/<c>dict&lt;K, V&gt;</c>, но
	/// ожидаемый тип его не принимает (spec §3): неявного преобразования между видами
	/// коллекций нет, поэтому это обычная несовместимость KRN2101, а не «нельзя
	/// преобразовать». <c>null</c> — литерал совместим.
	/// </summary>
	private BoundExpression? CollectionHintMismatch(KernType actual, KernType? hint, TextSpan span)
	{
		if (hint is null || IsError(hint) || Conversions.TryClassify(actual, hint, out _))
			return null;

		Report(
			"KRN2101",
			$"Литерал коллекции типа '{actual}' несовместим с ожидаемым '{hint}': неявного преобразования нет",
			span);
		return new BoundErrorExpression(span);
	}

	/// <summary>Тип элемента из ожидаемого <c>list&lt;T&gt;</c>; для остальных типов подсказки нет.</summary>
	private static KernType? ExpectedElementType(KernType? hint) =>
		hint is BuiltinGenericType { BuiltinKind: BuiltinGenericKind.List, Arguments.Count: 1 } list
			? list.Arguments[0]
			: null;

	/// <summary>
	/// Типы ключа и значения из ожидаемого <c>dict&lt;K, V&gt;</c>; для остальных типов
	/// подсказки нет (обе части <c>null</c>).
	/// </summary>
	private static (KernType? Key, KernType? Value) ExpectedDictionaryTypes(KernType? hint) =>
		hint is BuiltinGenericType { BuiltinKind: BuiltinGenericKind.Dict, Arguments.Count: 2 } dict
			? (dict.Arguments[0], dict.Arguments[1])
			: (null, null);

	/// <summary>
	/// Подходит ли ожидаемый тип элемента (spec §3, «числовое повышение»): подсказка выбирает
	/// только расширение чисел — новых преобразований коллекций она не добавляет, поэтому
	/// spread обязан совпасть с <paramref name="elementHint"/> точно.
	/// </summary>
	private static bool HintFits(IReadOnlyList<BoundCollectionItem> items, KernType elementHint)
	{
		if (!IsNumeric(elementHint))
			return false;

		foreach (BoundCollectionItem item in items)
		{
			if (item.IsSpread)
			{
				if (CollectionElementType(item.Value.Type) is not { } spreadElement ||
					!spreadElement.Equals(elementHint))
					return false;
				continue;
			}

			if (item.Value.Type.Equals(elementHint))
				continue;
			if (!Conversions.TryClassify(item.Value.Type, elementHint, out _))
				return false;
		}

		return true;
	}

	/// <summary>
	/// Подходит ли подсказка <c>K</c>/<c>V</c> для набора типов ключей или значений (spec §3):
	/// как и у списка, подсказка выбирает только числовое расширение, а не новые
	/// преобразования коллекций.
	/// </summary>
	private static bool HintFitsTypes(IReadOnlyList<KernType> types, KernType hint)
	{
		if (!IsNumeric(hint))
			return false;

		foreach (KernType type in types)
		{
			if (type.Equals(hint))
				continue;
			if (!Conversions.TryClassify(type, hint, out _))
				return false;
		}

		return true;
	}

	/// <summary>
	/// Объединение типов элементов (spec §3): одинаковые типы сохраняются, числа расширяются
	/// до самого широкого в семействе, смешение <c>inum</c> и <c>fnum</c> без явного типа —
	/// ошибка. <paramref name="label"/> называет объединяемые типы в диагностике
	/// («элементов списка», «ключей словаря», «значений словаря»). <c>null</c> означает, что
	/// диагностика уже выдана.
	/// </summary>
	private KernType? UnifyElementTypes(
		IReadOnlyList<KernType> candidates,
		TextSpan span,
		string label = "элементов списка")
	{
		KernType result = candidates[0];
		for (var index = 1; index < candidates.Count; index++)
		{
			KernType candidate = candidates[index];
			if (result.Equals(candidate))
				continue;
			if (!IsNumeric(result) || !IsNumeric(candidate))
			{
				Report("KRN2101", $"Нет общего типа {label}: '{result}' и '{candidate}'", span);
				return null;
			}

			// spec §3: смешивание inum и fnum без явного типа — ошибка. Числовое расширение
			// работает только внутри семейства (i32→i64, f32→f64), а не между ними.
			if (IsIntegralFamily(result) && IsFloatFamily(candidate) ||
				IsFloatFamily(result) && IsIntegralFamily(candidate))
			{
				Report(
					"KRN2101",
					$"Нельзя смешивать целые и вещественные типы {label} ('{result}' и '{candidate}') без явного типа: укажите тип явно, например '<f64>[...]'",
					span);
				return null;
			}

			if (!TryWidenNumeric(result, candidate, out KernType widened))
			{
				Report(
					"KRN2101",
					$"Нельзя объединить числовые типы {label} '{result}' и '{candidate}': нужен явный тип, например '<i64>[...]'",
					span);
				return null;
			}

			result = widened;
		}

		if (result is NilType or PrimitiveType
			{ Primitive: PrimitiveKind.None or PrimitiveKind.Void or PrimitiveKind.Never })
		{
			Report(
				"KRN2102",
				$"Нельзя вывести тип {label} из '{result}': укажите его явно, например '<i32>[...]'",
				span);
			return null;
		}

		return result;
	}

	/// <summary>
	/// Целое семейство (<c>i32</c>, <c>u64</c>, <c>inum</c>); <c>num</c> сюда не входит — он
	/// не integral и не float, а универсальный (spec §3).
	/// </summary>
	private static bool IsIntegralFamily(KernType type) =>
		type is PrimitiveType primitive && NumericRules.IsIntegral(primitive.Primitive);

	/// <summary>Вещественное семейство (<c>f32</c>, <c>f64</c>, <c>fnum</c>).</summary>
	private static bool IsFloatFamily(KernType type) =>
		type is PrimitiveType primitive && NumericRules.IsFloat(primitive.Primitive);

	/// <summary>Числовое расширение в одну сторону: <c>i32</c>+<c>i64</c> → <c>i64</c> (spec §3, §15).</summary>
	private static bool TryWidenNumeric(KernType left, KernType right, out KernType widened)
	{
		widened = left;
		if (left is not PrimitiveType leftPrimitive || right is not PrimitiveType rightPrimitive)
			return false;
		if (NumericRules.IsWidening(rightPrimitive.Primitive, leftPrimitive.Primitive))
			return true;
		if (!NumericRules.IsWidening(leftPrimitive.Primitive, rightPrimitive.Primitive))
			return false;

		widened = right;
		return true;
	}

	/// <summary>Тип элемента <c>list&lt;T&gt;</c>/<c>array&lt;T&gt;</c>; для остальных — <c>null</c>.</summary>
	private static KernType? CollectionElementType(KernType type) =>
		type is BuiltinGenericType
		{
			BuiltinKind: BuiltinGenericKind.List or BuiltinGenericKind.Array,
			Arguments.Count: 1
		} builtin
			? builtin.Arguments[0]
			: null;
}
