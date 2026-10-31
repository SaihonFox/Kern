using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.CodeGen;

/// <summary>
/// Генерация литералов коллекций (spec §3). Неявный <c>[a, ..b, c]</c> собирается в
/// <c>list&lt;T&gt;</c>: <c>NewList</c> принимает число элементов одним байтом, поэтому
/// короткий литерал без spread строится одной инструкцией, а всё остальное — пустым
/// списком плюс поштучные <c>ListAppend</c>/<c>ListAppendRange</c>. Явный array-литерал
/// (<c>array&lt;T&gt;</c> в ожидаемом типе) всегда без spread и строится одной <c>NewArray</c>.
/// </summary>
public sealed partial class CodeGenerator
{
	/// <summary>Максимум элементов, который <c>NewList</c> умеет взять со стека (операнд — байт).</summary>
	private const int MaxNewListItems = 255;

	private void EmitCollectionLiteral(BoundCollectionLiteral literal)
	{
		var elementType = ElementTypeOf(literal.Type);
		var signature = _module.InternTypeSig(TypeSigMap.From(elementType, _module.Name));
		if (IsArrayLiteral(literal))
		{
			// B3b1: array-литерал не содержит spread (binder отвергает его), поэтому
			// элементы вычисляются по порядку и снимаются одной инструкцией.
			foreach (BoundCollectionItem item in literal.Items)
			{
				EmitExpression(item.Value);
				EmitCopyIfUnit(item.Value.Type);
			}

			Emit(OpCode.NewArray, signature);
			EmitOperand(literal.Items.Count);
			return;
		}

		var hasSpread = literal.Items.Any(item => item.IsSpread);
		if (!hasSpread && literal.Items.Count <= MaxNewListItems)
		{
			// Порядок сохраняется: PopItems снимает значения с вершины и раскладывает их
			// по индексам, поэтому элемент 0 должен быть вычислен первым.
			foreach (BoundCollectionItem item in literal.Items)
			{
				EmitExpression(item.Value);
				EmitCopyIfUnit(item.Value.Type);
			}

			Emit(OpCode.NewList, signature);
			EmitOperand(literal.Items.Count);
			return;
		}

		Emit(OpCode.NewList, signature);
		EmitOperand(0);
		foreach (BoundCollectionItem item in literal.Items)
		{
			EmitExpression(item.Value);
			if (item.IsSpread)
			{
				// Источник spread'а уже list<T>/array<T>; элементы копируются по одному,
				// поэтому результат не делит хранилище с исходной коллекцией.
				Emit(OpCode.ListAppendRange);
				continue;
			}

			EmitCopyIfUnit(item.Value.Type);
			Emit(OpCode.ListAppend);
		}
	}

	/// <summary>Array-литерал: <see cref="BoundCollectionLiteral.Type"/> — <c>array&lt;T&gt;</c>.</summary>
	private static bool IsArrayLiteral(BoundCollectionLiteral literal) =>
		literal.Type is BuiltinGenericType { BuiltinKind: BuiltinGenericKind.Array };

	/// <summary>
	/// Тип элемента из типа литерала (<c>list&lt;T&gt;</c> или <c>array&lt;T&gt;</c>). Ошибка
	/// типа сюда не доходит (codegen получает уже связанный узел), но на всякий случай
	/// используется <c>object</c>.
	/// </summary>
	private static KernType ElementTypeOf(KernType type) =>
		type is BuiltinGenericType
		{
			BuiltinKind: BuiltinGenericKind.List or BuiltinGenericKind.Array,
			Arguments.Count: 1
		} builtin
			? builtin.Arguments[0]
			: PrimitiveType.Object;

	private void ReportCollection(string code, string message, TextSpan span) => Report(code, message, span);

	/// <summary>
	/// Литерал словаря (spec §3, B3b2): <c>NewDict</c> создаёт пустой <c>dict&lt;K, V&gt;</c>,
	/// затем каждая пара вычисляется по порядку (ключ ниже значения) и пишется <c>DictSet</c>,
	/// который оставляет словарь на стеке. Повтор ключа допустим: побеждает последняя запись.
	/// </summary>
	private void EmitDictionaryLiteral(BoundDictionaryLiteral literal)
	{
		var (keyType, valueType) = DictionaryTypesOf(literal.Type);
		var keySignature = _module.InternTypeSig(TypeSigMap.From(keyType, _module.Name));
		var valueSignature = _module.InternTypeSig(TypeSigMap.From(valueType, _module.Name));
		Emit(OpCode.NewDict, keySignature);
		EmitOperand(valueSignature);
		foreach (BoundCollectionEntry entry in literal.Entries)
		{
			EmitExpression(entry.Key);
			EmitCopyIfUnit(entry.Key.Type);
			EmitExpression(entry.Value);
			EmitCopyIfUnit(entry.Value.Type);
			Emit(OpCode.DictSet);
		}
	}

	/// <summary>
	/// B5b1/B5b2: встроенная индексация (spec §3). Для <c>list</c>/<c>array</c> на стек
	/// кладутся цель и индекс <c>i32</c> — <c>ListGet</c>; для <c>str</c> — <c>StrGet</c>
	/// (результат <c>char</c>). Для <c>dict</c> — цель и ключ, <c>DictGet</c>; отсутствующий
	/// ключ даёт панику. Отрицательный индекс нормализует VM.
	/// </summary>
	private void EmitIndex(BoundIndexExpression index)
	{
		EmitExpression(index.Target);
		EmitExpression(index.Index);
		Emit(index.Target.Type switch
		{
			BuiltinGenericType { BuiltinKind: BuiltinGenericKind.Dict } => OpCode.DictGet,
			PrimitiveType { Primitive: PrimitiveKind.Str } => OpCode.StrGet,
			_ => OpCode.ListGet,
		});
	}

	/// <summary>
	/// B5b2: срез <c>target[start..end]</c>. На стек кладутся цель, начало и конец <c>i32</c>,
	/// затем <c>Slice</c> с флагом включительного конца (1/0). VM нормализует отрицательные
	/// границы, проверяет диапазон (паника) и копирует элементы в новый <c>list&lt;T&gt;</c>/<c>str</c>.
	/// </summary>
	private void EmitSlice(BoundSliceExpression slice)
	{
		EmitExpression(slice.Target);
		EmitExpression(slice.Start);
		EmitExpression(slice.End);
		Emit(OpCode.Slice, slice.Inclusive ? 1 : 0);
	}

	/// <summary>
	/// Типы ключа и значения из типа литерала (<c>dict&lt;K, V&gt;</c>). Ошибка типа сюда не
	/// доходит, но на всякий случай используется <c>object</c>.
	/// </summary>
	private static (KernType Key, KernType Value) DictionaryTypesOf(KernType type) =>
		type is BuiltinGenericType { BuiltinKind: BuiltinGenericKind.Dict, Arguments.Count: 2 } dict
			? (dict.Arguments[0], dict.Arguments[1])
			: (PrimitiveType.Object, PrimitiveType.Object);
}
