using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

public sealed partial class Binder
{
	/// <summary>Каноническое имя пользовательского индексатора (B5a).</summary>
	private const string IndexOperator = "[]";

	/// <summary>
	/// Индексация <c>x[a, b]</c>: пользовательский <c>op [](...)</c> (B5a) либо встроенное
	/// правило коллекций (B5b1) и строк (B5b2). Встроенные <c>list</c>/<c>array</c>/<c>dict</c>
	/// и <c>str</c> никогда не ищут пользовательский оператор. Пользовательский результат —
	/// обычный вызов с приёмником: codegen передаёт его первым аргументом, как у метода экземпляра.
	/// </summary>
	private BoundExpression BindIndexExpression(IndexExpression index)
	{
		var target = BindExpression(index.Target);
		if (IsError(target.Type))
			return new BoundErrorExpression(index.Span);

		// B5b1: встроенная индексация коллекций (spec §3, round 27).
		if (target.Type is BuiltinGenericType
			{
				BuiltinKind: BuiltinGenericKind.List or BuiltinGenericKind.Array or BuiltinGenericKind.Dict
			} builtin)
		{
			return BindBuiltinIndex(index, target, builtin);
		}

		// B5b2: str читается по индексу (char) и режется (новая str).
		if (target.Type is PrimitiveType { Primitive: PrimitiveKind.Str })
			return BindStrIndex(index, target);

		var owner = OwnerOf(target.Type);
		if (owner is null)
		{
			Report("KRN2101", $"Тип '{target.Type}' не поддерживает индексацию", index.Span);
			return new BoundErrorExpression(index.Span);
		}

		var candidates = new List<FunctionSymbol>();
		foreach (var function in owner.Members.Operators)
			if (function.OpSymbol == IndexOperator && function.Parameters.Count == index.Arguments.Count)
				candidates.Add(function);

		if (candidates.Count == 0)
		{
			Report(
				"KRN2101",
				$"У типа '{owner.Name}' нет индексатора '[]' для {index.Arguments.Count} аргументов",
				index.Span);
			return new BoundErrorExpression(index.Span);
		}

		var arguments = new List<BoundExpression>(index.Arguments.Count);
		foreach (var argument in index.Arguments)
			arguments.Add(BindExpression(argument));

		var selected = SelectIndexer(candidates, arguments);
		if (selected.ReturnType is null)
		{
			Report(
				"KRN2101",
				$"Индексатор '[]' типа '{owner.Name}' должен объявлять результат '-> R'",
				index.Span);
			return new BoundErrorExpression(index.Span);
		}

		var converted = new List<BoundExpression>(arguments.Count);
		for (var position = 0; position < arguments.Count; position++)
			converted.Add(Convert(arguments[position], selected.Parameters[position].Type, arguments[position].Span));

		return new BoundCallExpression(selected, converted, CallResultType(selected, selected.ReturnType), index.Span, target)
		{
			// Q30: пользовательский индексатор класса диспатчится по типу получателя.
			Virtual = IsVirtualCall(target, selected),
		};
	}

	/// <summary>
	/// B5b1: встроенная индексация коллекций (spec §3, round 27). <c>xs[i]</c> у
	/// <c>list&lt;T&gt;</c>/<c>array&lt;T&gt;</c> принимает один индекс, приводимый к <c>i32</c>,
	/// и даёт <c>T</c>; <c>d[k]</c> у <c>dict&lt;K, V&gt;</c> — один ключ, приводимый к <c>K</c>,
	/// и даёт <c>V</c>. Срез (B5b2) — один диапазон без шага и даёт новый <c>list&lt;T&gt;</c>;
	/// лишние индексы, срез словаря и шаг — KRN2101, несовместимый индекс — KRN2102
	/// (его выдаёт <see cref="Convert"/>).
	/// </summary>
	private BoundExpression BindBuiltinIndex(
		IndexExpression index,
		BoundExpression target,
		BuiltinGenericType builtin)
	{
		if (index.Arguments.Count != 1)
		{
			Report(
				"KRN2101",
				$"Индексация '{builtin}' принимает один индекс, получено {index.Arguments.Count}",
				index.Span);
			return new BoundErrorExpression(index.Span);
		}

		// B5b2: срез диапазоном; у dict срезов нет, array режется в новый list<T>.
		if (index.Arguments[0] is RangeExpression range)
		{
			if (builtin.BuiltinKind == BuiltinGenericKind.Dict)
			{
				Report("KRN2101", "Срез словаря не поддержан: индексируйте dict ключом", range.Span);
				return new BoundErrorExpression(index.Span);
			}

			var listType = new BuiltinGenericType(BuiltinGenericKind.List, [builtin.Arguments[0]]);
			return BindSlice(index, target, range, listType);
		}

		var isDictionary = builtin.BuiltinKind == BuiltinGenericKind.Dict;
		var indexType = isDictionary ? builtin.Arguments[0] : PrimitiveType.I32;
		var resultType = isDictionary ? builtin.Arguments[1] : builtin.Arguments[0];

		var argument = BindExpression(index.Arguments[0]);
		if (IsError(argument.Type))
			return new BoundErrorExpression(index.Span);

		var converted = Convert(argument, indexType, argument.Span);
		if (IsError(converted.Type))
			return new BoundErrorExpression(index.Span);

		return new BoundIndexExpression(target, converted, resultType, index.Span);
	}

	/// <summary>
	/// B5b2: встроенная индексация <c>str</c> (spec §3, round 27). <c>s[i]</c> принимает один
	/// индекс, приводимый к <c>i32</c>, и даёт <c>char</c>; отрицательный индекс считается
	/// с конца, выход за границы — паника VM. <c>s[a..b]</c>/<c>s[a..=b]</c> — новая <c>str</c>.
	/// </summary>
	private BoundExpression BindStrIndex(IndexExpression index, BoundExpression target)
	{
		if (index.Arguments.Count != 1)
		{
			Report(
				"KRN2101",
				$"Индексация str принимает один индекс, получено {index.Arguments.Count}",
				index.Span);
			return new BoundErrorExpression(index.Span);
		}

		if (index.Arguments[0] is RangeExpression range)
			return BindSlice(index, target, range, PrimitiveType.Str);

		var argument = BindExpression(index.Arguments[0]);
		if (IsError(argument.Type))
			return new BoundErrorExpression(index.Span);

		var converted = Convert(argument, PrimitiveType.I32, argument.Span);
		if (IsError(converted.Type))
			return new BoundErrorExpression(index.Span);

		return new BoundIndexExpression(target, converted, PrimitiveType.Char, index.Span);
	}

	/// <summary>
	/// spec §5: запись по встроенному индексу. <c>xs[i] = v</c> у <c>list&lt;T&gt;</c>/
	/// <c>array&lt;T&gt;</c> и <c>d[k] = v</c> у <c>dict&lt;K, V&gt;</c> мутируют коллекцию;
	/// <c>str</c> и пользовательский <c>op []</c> остаются только для чтения (KRN2106).
	/// Составное присваивание вычисляет цель и индекс один раз во временные локальные,
	/// читает текущее значение и записывает объединённое.
	/// </summary>
	private BoundStatement BindIndexAssignment(AssignmentStatement assignment, IndexExpression index)
	{
		var target = BindExpression(index.Target);
		if (IsError(target.Type))
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);

		if (target.Type is not BuiltinGenericType
			{
				BuiltinKind: BuiltinGenericKind.List or BuiltinGenericKind.Array or BuiltinGenericKind.Dict
			} builtin)
		{
			Report(
				"KRN2106",
				"Индексатор только для чтения: запись по индексу поддержана для list, array и dict",
				index.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		if (index.Arguments.Count != 1)
		{
			Report(
				"KRN2101",
				$"Запись по индексу '{builtin}' принимает один индекс, получено {index.Arguments.Count}",
				index.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		if (index.Arguments[0] is RangeExpression range)
		{
			Report("KRN2106", "Запись срезом не поддержана: срез возвращает новую коллекцию", range.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		var isDictionary = builtin.BuiltinKind == BuiltinGenericKind.Dict;
		var indexType = isDictionary ? builtin.Arguments[0] : PrimitiveType.I32;
		var valueType = isDictionary ? builtin.Arguments[1] : builtin.Arguments[0];

		var argument = BindExpression(index.Arguments[0]);
		if (IsError(argument.Type))
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);

		var convertedIndex = Convert(argument, indexType, argument.Span);
		if (IsError(convertedIndex.Type))
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);

		if (assignment.Operator != "=")
			return BindCompoundIndexAssignment(assignment, target, convertedIndex, valueType, index);

		var convertedValue = Convert(BindExpression(assignment.Value, valueType), valueType, assignment.Value.Span);
		if (IsError(convertedValue.Type))
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);

		return new BoundIndexStore(target, convertedIndex, convertedValue, assignment.Span);
	}

	/// <summary>
	/// Составное присваивание по индексу (<c>xs[i] += 1</c>, <c>d[k] *= 2</c>): цель и индекс
	/// вычисляются один раз во временные локальные (имена с <c>#</c>), затем текущее значение
	/// читается через <see cref="BoundIndexExpression"/> и комбинируется обычным оператором.
	/// У словаря отсутствующий ключ читается как паника VM — как и у <c>d[k]</c>.
	/// </summary>
	private BoundStatement BindCompoundIndexAssignment(
		AssignmentStatement assignment,
		BoundExpression target,
		BoundExpression index,
		KernType valueType,
		IndexExpression indexSyntax)
	{
		var statements = new List<BoundStatement>(3);

		BoundVariable targetVariable = DeclareLocal($"#indexTarget{_nextSlot}", target.Type, false, indexSyntax.Span);
		DeclareFlow(targetVariable, true, target.Type);
		statements.Add(new BoundLocalDeclaration(
			targetVariable.Name,
			targetVariable.Slot,
			target.Type,
			target,
			false,
			indexSyntax.Span));

		BoundVariable indexVariable = DeclareLocal($"#indexKey{_nextSlot}", index.Type, false, indexSyntax.Span);
		DeclareFlow(indexVariable, true, index.Type);
		statements.Add(new BoundLocalDeclaration(
			indexVariable.Name,
			indexVariable.Slot,
			index.Type,
			index,
			false,
			indexSyntax.Span));

		var targetReference = new BoundVariableExpression(
			targetVariable.Name,
			targetVariable.Slot,
			target.Type,
			indexSyntax.Span);
		var indexReference = new BoundVariableExpression(
			indexVariable.Name,
			indexVariable.Slot,
			index.Type,
			indexSyntax.Span);

		var current = new BoundIndexExpression(targetReference, indexReference, valueType, indexSyntax.Span);
		var symbol = assignment.Operator.TrimEnd('=');
		var combined = BindBinaryOperator(symbol, current, BindExpression(assignment.Value), assignment.Span);
		if (combined is BoundErrorExpression)
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);

		var converted = Convert(combined, valueType, assignment.Value.Span);
		if (IsError(converted.Type))
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);

		statements.Add(new BoundIndexStore(targetReference, indexReference, converted, assignment.Span));
		return new BoundBlock(statements, null, FallsThrough: true, assignment.Span);
	}

	/// <summary>
	/// B5b2: срез <c>target[start..end]</c>/<c>target[start..=end]</c> и шаг
	/// <c>target[start..end:step]</c> (spec §3, round 27). Границы/шаг приводятся к
	/// <c>i32</c> (несовпадение — KRN2102); положительность шага и границы проверяет VM.
	/// </summary>
	private BoundExpression BindSlice(
		IndexExpression index,
		BoundExpression target,
		RangeExpression range,
		KernType resultType)
	{
		var start = Convert(BindExpression(range.Start), PrimitiveType.I32, range.Start.Span);
		if (IsError(start.Type))
			return new BoundErrorExpression(index.Span);

		var end = Convert(BindExpression(range.End), PrimitiveType.I32, range.End.Span);
		if (IsError(end.Type))
			return new BoundErrorExpression(index.Span);

		BoundExpression? step = null;
		if (range.Step is not null)
		{
			step = Convert(BindExpression(range.Step), PrimitiveType.I32, range.Step.Span);
			if (IsError(step.Type))
				return new BoundErrorExpression(index.Span);
		}

		return new BoundSliceExpression(target, start, end, range.IsInclusive, resultType, index.Span, step);
	}

	/// <summary>
	/// Выбор индексатора одной арности: точное совпадение типов важнее преобразования.
	/// Если не подходит ни один, остаётся первый — его <see cref="Convert"/> и выдаст KRN2102.
	/// </summary>
	private static FunctionSymbol SelectIndexer(
		IReadOnlyList<FunctionSymbol> candidates,
		IReadOnlyList<BoundExpression> arguments)
	{
		if (candidates.Count == 1)
			return candidates[0];

		foreach (var candidate in candidates)
		{
			var exact = true;
			for (var position = 0; position < arguments.Count; position++)
			{
				if (arguments[position].Type.Equals(candidate.Parameters[position].Type))
					continue;
				exact = false;
				break;
			}

			if (exact)
				return candidate;
		}

		foreach (var candidate in candidates)
		{
			var convertible = true;
			for (var position = 0; position < arguments.Count; position++)
			{
				if (IsError(arguments[position].Type) ||
					!Conversions.TryClassify(arguments[position].Type, candidate.Parameters[position].Type, out _))
				{
					convertible = false;
					break;
				}
			}

			if (convertible)
				return candidate;
		}

		return candidates[0];
	}
}
