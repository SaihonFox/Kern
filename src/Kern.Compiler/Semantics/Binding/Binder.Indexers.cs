using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Overloads;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

public sealed partial class Binder
{
	/// <summary>Каноническое имя пользовательского индексатора (B5a).</summary>
	private const string IndexOperator = "[]";

	/// <summary>Каноническое имя пользовательского сеттера индексатора (spec §5).</summary>
	private const string IndexSetOperator = "[]=";

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
		var candidates = new List<FunctionSymbol>();
		if (owner is not null)
			foreach (var function in owner.Members.Operators)
				if (function.OpSymbol == IndexOperator && function.Parameters.Count == index.Arguments.Count)
					candidates.Add(function);

		KernType? extensionReceiver = null;
		if (candidates.Count == 0)
		{
			foreach (var extensions in _moduleScope.FindExtensionsFor(target.Type))
				foreach (var function in extensions.GetFunctions(IndexOperator))
					if (function.Parameters.Count == index.Arguments.Count)
					{
						candidates.Add(function);
						extensionReceiver = target.Type;
					}
		}

		if (candidates.Count == 0)
		{
			string typeName = owner?.Name ?? target.Type.ToString();
			Report(
				"KRN2101",
				$"У типа '{typeName}' нет индексатора '[]' для {index.Arguments.Count} аргументов",
				index.Span);
			return new BoundErrorExpression(index.Span);
		}

		var arguments = new List<BoundExpression>(index.Arguments.Count);
		foreach (var argument in index.Arguments)
			arguments.Add(BindExpression(argument));

		var selected = SelectIndexer(candidates, arguments, index.Span, extensionReceiver);
		if (selected is null)
			return new BoundErrorExpression(index.Span);
		if (selected.ReturnType is null)
		{
			string typeName = owner?.Name ?? target.Type.ToString();
			Report(
				"KRN2101",
				$"Индексатор '[]' типа '{typeName}' должен объявлять результат '-> R'",
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
	/// Пользовательский сеттер индексатора <c>op []=</c> (spec §5): простой <c>x[i] = v</c>
	/// вызывает его напрямую; составное <c>x[i] += v</c> один раз вычисляет приёмник и индексы,
	/// читает текущее значение через <c>op []</c>, комбинирует и вызывает сеттер.
	/// </summary>
	private BoundStatement BindUserIndexAssignment(
		AssignmentStatement assignment,
		IndexExpression index,
		BoundExpression target,
		IReadOnlyList<FunctionSymbol> setters)
	{
		var indexArguments = new List<BoundExpression>(index.Arguments.Count);
		foreach (ExpressionNode argument in index.Arguments)
		{
			BoundExpression bound = BindExpression(argument);
			if (IsError(bound.Type))
				return new BoundBlock([], null, FallsThrough: true, assignment.Span);
			indexArguments.Add(bound);
		}

		FunctionSymbol? setter = SelectUserIndexer(
			setters, indexArguments, hasValueParameter: true, span: index.Span, out bool ambiguity);
		if (setter is null)
		{
			if (!ambiguity)
				Report("KRN2102", "Не найден подходящий op []= для аргументов индексатора", index.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		var convertedIndex = new List<BoundExpression>(indexArguments.Count);
		for (var position = 0; position < indexArguments.Count; position++)
		{
			BoundExpression converted = Convert(
				indexArguments[position], setter.Parameters[position].Type, indexArguments[position].Span);
			if (IsError(converted.Type))
				return new BoundBlock([], null, FallsThrough: true, assignment.Span);
			convertedIndex.Add(converted);
		}

		KernType valueType = setter.Parameters[^1].Type;
		if (assignment.Operator == "=")
		{
			BoundExpression value = Convert(
				BindExpression(assignment.Value, valueType), valueType, assignment.Value.Span);
			if (IsError(value.Type))
				return new BoundBlock([], null, FallsThrough: true, assignment.Span);
			var arguments = new List<BoundExpression>(convertedIndex.Count + 1);
			arguments.AddRange(convertedIndex);
			arguments.Add(value);
			return new BoundExpressionStatement(
				new BoundCallExpression(setter, arguments, CallResultType(setter, setter.ReturnType),
					assignment.Span, target),
				assignment.Span);
		}

		// Составное присваивание: приёмник и индексы — во временные локальные, чтобы
		// побочные эффекты не выполнились дважды.
		var statements = new List<BoundStatement>(convertedIndex.Count + 2);
		BoundVariable targetVariable = DeclareLocal($"#indexTarget{_nextSlot}", target.Type, false, assignment.Span);
		DeclareFlow(targetVariable, true, target.Type);
		statements.Add(new BoundLocalDeclaration(
			targetVariable.Name, targetVariable.Slot, target.Type, target, false, assignment.Span));
		var targetReference = new BoundVariableExpression(
			targetVariable.Name, targetVariable.Slot, target.Type, assignment.Span);

		var indexReferences = new List<BoundExpression>(convertedIndex.Count);
		for (var position = 0; position < convertedIndex.Count; position++)
		{
			KernType type = convertedIndex[position].Type;
			BoundVariable variable = DeclareLocal($"#indexKey{_nextSlot}", type, false, assignment.Span);
			DeclareFlow(variable, true, type);
			statements.Add(new BoundLocalDeclaration(
				variable.Name, variable.Slot, type, convertedIndex[position], false, assignment.Span));
			indexReferences.Add(new BoundVariableExpression(variable.Name, variable.Slot, type, assignment.Span));
		}

		var getters = new List<FunctionSymbol>();
		var owner = OwnerOf(target.Type);
		if (owner is not null)
			foreach (var function in owner.Members.Operators)
				if (function.OpSymbol == IndexOperator && function.Parameters.Count == indexReferences.Count)
					getters.Add(function);
		FunctionSymbol? getter = SelectUserIndexer(
			getters, indexReferences, hasValueParameter: false, span: index.Span, out bool getterAmbiguity);
		if (getter is null)
		{
			if (!getterAmbiguity)
				Report("KRN2101", "Для составного присваивания через op []= нужен парный op []", index.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		var getterCall = new BoundCallExpression(
			getter,
			indexReferences,
			CallResultType(getter, getter.ReturnType),
			index.Span,
			targetReference);
		var combined = BindBinaryOperator(
			assignment.Operator.TrimEnd('='),
			getterCall,
			BindExpression(assignment.Value),
			assignment.Span);
		if (combined is BoundErrorExpression)
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);

		BoundExpression combinedValue = Convert(combined, valueType, assignment.Value.Span);
		if (IsError(combinedValue.Type))
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);

		var setterArguments = new List<BoundExpression>(indexReferences.Count + 1);
		setterArguments.AddRange(indexReferences);
		setterArguments.Add(combinedValue);
		statements.Add(new BoundExpressionStatement(
			new BoundCallExpression(setter, setterArguments, CallResultType(setter, setter.ReturnType),
				assignment.Span, targetReference),
			assignment.Span));
		return new BoundBlock(statements, null, FallsThrough: true, assignment.Span);
	}

	private FunctionSymbol? SelectUserIndexer(
		IReadOnlyList<FunctionSymbol> candidates,
		IReadOnlyList<BoundExpression> arguments,
		bool hasValueParameter,
		TextSpan span,
		out bool reported)
	{
		reported = false;
		if (candidates.Count == 0)
			return null;

		var exact = new List<FunctionSymbol>();
		var convertible = new List<FunctionSymbol>();
		foreach (FunctionSymbol candidate in candidates)
		{
			if (hasValueParameter && candidate.Parameters.Count != arguments.Count + 1)
				continue;
			var isExact = true;
			var isConvertible = true;
			for (var position = 0; position < arguments.Count; position++)
			{
				KernType declared = candidate.Parameters[position].Type;
				if (!arguments[position].Type.Equals(declared))
					isExact = false;
				if (IsError(arguments[position].Type) ||
					!Conversions.TryClassify(arguments[position].Type, declared, out _))
					isConvertible = false;
			}
			if (isExact)
				exact.Add(candidate);
			else if (isConvertible)
				convertible.Add(candidate);
		}

		if (exact.Count == 1)
			return exact[0];
		if (convertible.Count == 1 && exact.Count == 0)
			return convertible[0];

		var tied = exact.Count > 1 ? exact : convertible;
		if (tied.Count > 1)
		{
			string name = hasValueParameter ? IndexSetOperator : IndexOperator;
			Report(
				"KRN1402",
				$"Неоднозначный индексатор '{name}': подходят — {string.Join(", ", tied.Select(candidate => candidate.Signature))}",
				span);
			reported = true;
		}
		return null;
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

		// spec §5: пользовательский `op []=` — сеттер индексатора. Ищем его до
		// встроенного правила, но только для не-встроенных типов.
		if (target.Type is not BuiltinGenericType &&
			target.Type is not PrimitiveType { Primitive: PrimitiveKind.Str })
		{
			var owner = OwnerOf(target.Type);
			var setters = new List<FunctionSymbol>();
			if (owner is not null)
				foreach (var function in owner.Members.Operators)
					if (function.OpSymbol == IndexSetOperator &&
						function.Parameters.Count == index.Arguments.Count + 1)
						setters.Add(function);
			if (setters.Count == 0)
				foreach (var extensions in _moduleScope.FindExtensionsFor(target.Type))
					foreach (var function in extensions.GetFunctions(IndexSetOperator))
						if (function.Parameters.Count == index.Arguments.Count + 1)
							setters.Add(function);
			if (setters.Count > 0)
				return BindUserIndexAssignment(assignment, index, target, setters);
		}

		if (target.Type is not BuiltinGenericType
			{
				BuiltinKind: BuiltinGenericKind.List or BuiltinGenericKind.Array or BuiltinGenericKind.Dict
			} builtin)
		{
			Report(
				"KRN2106",
				"Индексатор только для чтения: запись по индексу поддержана для list, array, dict и op []= ",
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
		// Открытое начало `xs[..b]` — это срез с нуля; открытый конец `xs[a..]` остаётся
		// null и codegen берёт длину коллекции (SliceFrom/SliceStepFrom).
		BoundExpression start = range.Start is null
			? IntLiteral("0", range.Span)
			: Convert(BindExpression(range.Start), PrimitiveType.I32, range.Start.Span);
		if (IsError(start.Type))
			return new BoundErrorExpression(index.Span);

		BoundExpression? end = null;
		if (range.End is not null)
		{
			end = Convert(BindExpression(range.End), PrimitiveType.I32, range.End.Span);
			if (IsError(end.Type))
				return new BoundErrorExpression(index.Span);
		}

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
	/// Выбор индексатора одной арности через общий резолвер перегрузок (spec §15): точное
	/// совпадение, затем преобразования; несколько равных кандидатов — <c>KRN1402</c>.
	/// </summary>
	private FunctionSymbol? SelectIndexer(
		IReadOnlyList<FunctionSymbol> candidates,
		IReadOnlyList<BoundExpression> arguments,
		TextSpan span,
		KernType? receiverType = null)
	{
		return ResolveIndexer(candidates, arguments, span, receiverType);
	}

	/// <summary>
	/// Общий резолвер для <c>op []</c> и <c>op []=</c>. Возвращает null после диагностики
	/// (неоднозначность или отсутствие применимого кандидата).
	/// </summary>
	private FunctionSymbol? ResolveIndexer(
		IReadOnlyList<FunctionSymbol> candidates,
		IReadOnlyList<BoundExpression> arguments,
		TextSpan span,
		KernType? receiverType = null)
	{
		if (candidates.Count == 0)
			return null;

		var callArguments = new List<CallArgument>(arguments.Count);
		foreach (BoundExpression argument in arguments)
		{
			NumericLiteralValue? constant = argument is BoundLiteralExpression { Numeric: { } numeric }
				? numeric
				: null;
			callArguments.Add(CallArgument.Positional(argument.Type, constant, argument.Span));
		}

		OverloadResolution resolution = OverloadResolver.Resolve(
			candidates,
			new CallArguments(callArguments, receiverType, span: span, name: IndexOperator));
		foreach (Diagnostic diagnostic in resolution.Diagnostics)
			Report(diagnostic.Code, diagnostic.Message, span);
		return resolution.Success ? resolution.Function : null;
	}
}
