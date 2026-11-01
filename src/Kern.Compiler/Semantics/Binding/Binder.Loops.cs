using Kern.Compiler.Semantics.Binding.Flow;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Итерационный <c>loop … in …</c> (spec §3, round 7/15): диапазон <c>1..6</c>/<c>1..=6</c>
/// (с <c>:step</c>) и <c>list</c>/<c>array</c>. Разворачивается в обычный <c>BoundLoopStatement</c>
/// по счётчику: элемент присваивается переменной цикла в начале каждой итерации.
/// </summary>
public sealed partial class Binder
{
	private BoundStatement BindIterationLoop(LoopExpression source, SyntaxNode body, TextSpan span)
	{
		var previousScope = _scope;
		_scope = new BoundScope(previousScope);
		try
		{
			var statements = new List<BoundStatement>();
			if (!TryBindIteration(source, statements, out var element, out _, out var condition, out var increment, out var iterationPrefix))
				return EmptyLoopBlock(span);

			var assignments = BindIterationVariables(source, element, span);
			if (assignments is null)
				return EmptyLoopBlock(span);

			var afterLoop = _flow.Clone();
			afterLoop.ClearNarrowing();
			var loopBody = BindLoopBodyWithFlow(null, body, source.Label, increment, out var breaks);

			var bodyStatements = new List<BoundStatement>(iterationPrefix.Count + assignments.Count + loopBody.Statements.Count + 1);
			bodyStatements.AddRange(iterationPrefix);
			bodyStatements.AddRange(assignments);
			bodyStatements.AddRange(loopBody.Statements);
			bodyStatements.Add(increment);

			var block = new BoundBlock(bodyStatements, null, loopBody.FallsThrough, loopBody.Span);
			statements.Add(new BoundLoopStatement(condition, block, IsDoWhile: false, IsInfinite: false, span)
			{
				Label = source.Label?.Name
			});
			_flow = breaks.Count > 0 ? FlowState.Join(breaks) : afterLoop;
			return new BoundBlock(statements, null, FallsThrough: true, span);
		}
		finally
		{
			_scope = previousScope;
		}
	}

	/// <summary>
	/// Переменные цикла: без переменной — скрытая; одна — объявление или запись в существующую
	/// (голое имя, spec §3); несколько — деструктуризация элемента-кортежа (пары `dict`).
	/// Возвращает присваивания, которые выполняются в начале каждой итерации.
	/// </summary>
	private List<BoundStatement>? BindIterationVariables(
		LoopExpression source,
		BoundExpression element,
		TextSpan span)
	{
		var assignments = new List<BoundStatement>(source.Bindings.Count + 1);
		if (source.Bindings.Count == 0)
		{
			var hidden = DeclareLocal($"#ignored{_nextSlot}", element.Type, false, span);
			DeclareFlow(hidden, true, element.Type);
			assignments.Add(new BoundAssignment(hidden.Name, hidden.Slot, element.Type, element, span));
			return assignments;
		}

		if (source.Bindings.Count == 1)
		{
			var binding = source.Bindings[0];
			var declared = binding.Type is null ? null : _resolver.Resolve(binding.Type, _typeScope);
			if (declared is null && !binding.IsMutable && _scope.Lookup(binding.Name) is { } existing)
			{
				var value = Convert(element, existing.Type, binding.Span);
				if (IsError(value.Type))
					return null;
				assignments.Add(new BoundAssignment(existing.Name, existing.Slot, existing.Type, value, binding.Span));
				return assignments;
			}

			var type = declared ?? element.Type;
			var converted = Convert(element, type, binding.Span);
			if (IsError(converted.Type))
				return null;
			var variable = DeclareLocal(binding.Name, type, false, binding.Span);
			DeclareFlow(variable, true, type);
			assignments.Add(new BoundAssignment(variable.Name, variable.Slot, type, converted, binding.Span));
			return assignments;
		}

		if (element.Type is not TupleType tuple || tuple.Elements.Count != source.Bindings.Count)
		{
			Report(
				"KRN2102",
				"Деструктуризация переменных цикла требует кортеж той же арности",
				span);
			return null;
		}

		var item = DeclareLocal($"#item{_nextSlot}", element.Type, false, span);
		DeclareFlow(item, true, element.Type);
		assignments.Add(new BoundAssignment(item.Name, item.Slot, element.Type, element, span));
		var itemExpression = new BoundVariableExpression(item.Name, item.Slot, element.Type, span);

		for (var index = 0; index < source.Bindings.Count; index++)
		{
			var binding = source.Bindings[index];
			var declared = binding.Type is null ? null : _resolver.Resolve(binding.Type, _typeScope);
			var type = declared ?? tuple.Elements[index].Type;
			var access = new BoundTupleItemExpression(itemExpression, index, tuple.Elements[index].Type, binding.Span);
			var value = Convert(access, type, binding.Span);
			if (IsError(value.Type))
				return null;

			if (declared is null && !binding.IsMutable && _scope.Lookup(binding.Name) is { } existing)
			{
				var existingValue = Convert(value, existing.Type, binding.Span);
				if (IsError(existingValue.Type))
					return null;
				assignments.Add(new BoundAssignment(
					existing.Name, existing.Slot, existing.Type, existingValue, binding.Span));
				continue;
			}

			var variable = DeclareLocal(binding.Name, type, false, binding.Span);
			DeclareFlow(variable, true, type);
			assignments.Add(new BoundAssignment(variable.Name, variable.Slot, type, value, binding.Span));
		}

		return assignments;
	}

	/// <summary>
	/// Итерационный loop-выражение: результат — <c>list</c> значений тела
	/// (<c>list&lt;i32&gt; squares = loop i32 i in 1..5 i ** 2</c>, spec §3).
	/// </summary>
	private BoundExpression BindIterationLoopExpression(LoopExpression loop)
	{
		if (loop.Bindings.Count > 1)
		{
			Report("KRN2101", "Итерационный loop ожидает не более одной переменной", loop.Span);
			return new BoundErrorExpression(loop.Span);
		}

		var binding = loop.Bindings.Count == 0 ? null : loop.Bindings[0];
		var declared = binding?.Type is null ? null : _resolver.Resolve(binding.Type, _typeScope);
		var previousScope = _scope;
		_scope = new BoundScope(previousScope);
		try
		{
			var statements = new List<BoundStatement>();
			if (!TryBindIteration(loop, statements, out var element, out var elementType, out var condition, out var increment, out var iterationPrefix))
				return new BoundErrorExpression(loop.Span);

			var bindingSpan = binding?.Span ?? loop.Span;
			var variable = DeclareLocal(
				binding?.Name ?? $"#ignored{_nextSlot}",
				declared ?? elementType,
				false,
				bindingSpan);
			var loopType = declared ?? elementType;
			element = Convert(element, loopType, bindingSpan);
			if (IsError(element.Type))
				return new BoundErrorExpression(loop.Span);
			DeclareFlow(variable, true, loopType);

			var afterLoop = _flow.Clone();
			afterLoop.ClearNarrowing();
			var loopBody = BindLoopBodyWithFlow(null, loop.Body, loop.Label, increment, out var breaks);

			if (loopBody.Value is null || IsVoid(loopBody.Value.Type))
			{
				// Тело без значения (spec §3: «результат — list значений тела либо void»):
				// цикл остаётся оператором, а выражение имеет тип void. Это нужно ветвям
				// `match`/`if` в позиции оператора (`Ok(rows) => loop … println(…)`).
				var voidBody = new List<BoundStatement>(iterationPrefix.Count + loopBody.Statements.Count + 2);
				voidBody.AddRange(iterationPrefix);
				voidBody.Add(new BoundAssignment(variable.Name, variable.Slot, loopType, element, bindingSpan));
				voidBody.AddRange(loopBody.Statements);
				voidBody.Add(increment);
				statements.Add(new BoundLoopStatement(
					condition,
					new BoundBlock(voidBody, null, loopBody.FallsThrough, loopBody.Span),
					IsDoWhile: false,
					IsInfinite: false,
					loop.Span)
				{
					Label = loop.Label?.Name
				});
				_flow = breaks.Count > 0 ? FlowState.Join(breaks) : afterLoop;
				return new BoundBlockExpression(statements, null, FallsThrough: true, PrimitiveType.Void, loop.Span);
			}

			var valueType = loopBody.Value.Type;
			var listType = new BuiltinGenericType(BuiltinGenericKind.List, [valueType]);
			var list = DeclareLocal($"#list{_nextSlot}", listType, false, loop.Span);
			DeclareFlow(list, true, listType);
			statements.Add(new BoundLocalDeclaration(
				list.Name,
				list.Slot,
				listType,
				new BoundCollectionLiteral([], listType, loop.Span),
				false,
				loop.Span));

			var bodyStatements = new List<BoundStatement>(iterationPrefix.Count + loopBody.Statements.Count + 1);
			bodyStatements.AddRange(iterationPrefix);
			bodyStatements.Add(new BoundAssignment(variable.Name, variable.Slot, loopType, element, bindingSpan));
			bodyStatements.AddRange(loopBody.Statements);
			var bodyBlock = new BoundBlock(bodyStatements, loopBody.Value, loopBody.FallsThrough, loopBody.Span);

			var expression = new BoundLoopExpression(condition, bodyBlock, false, false, listType, loop.Span)
			{
				ListSlot = list.Slot,
				Increment = increment,
				Label = loop.Label?.Name
			};
			_flow = breaks.Count > 0 ? FlowState.Join(breaks) : afterLoop;
			return new BoundBlockExpression(statements, expression, FallsThrough: true, listType, loop.Span);
		}
		finally
		{
			_scope = previousScope;
		}
	}

	/// <summary>Подготовка счётчика/предела и выражений элемента, условия и инкремента.</summary>
	/// <param name="prefix">Операторы начала каждой итерации (протокол <c>iter&lt;T&gt;</c>); обычно пусто.</param>
	private bool TryBindIteration(
		LoopExpression source,
		List<BoundStatement> statements,
		out BoundExpression element,
		out KernType elementType,
		out BoundExpression condition,
		out BoundStatement increment,
		out List<BoundStatement> prefix)
	{
		prefix = [];
		if (source.Iterable is RangeExpression range)
			return TryBindRangeIteration(range, statements, out element, out elementType, out condition, out increment);

		var iterable = BindExpression(source.Iterable!);
		if (IsError(iterable.Type))
		{
			element = new BoundErrorExpression(source.Span);
			elementType = ErrorType.Instance;
			condition = element;
			increment = EmptyLoopBlock(source.Span);
			return false;
		}

		// `dict` итерируется парами (ключ, значение), `str` — по символам.
		if (iterable.Type is BuiltinGenericType { BuiltinKind: BuiltinGenericKind.Dict } dictionary &&
			dictionary.Arguments.Count == 2)
		{
			FunctionSymbol? keys = null;
			foreach (var native in NativeMembers(iterable.Type, "keys"))
			{
				if (native.Parameters.Count == 0)
				{
					keys = native;
					break;
				}
			}

			if (keys is null)
			{
				Report("KRN2101", $"У типа '{iterable.Type}' нет членов 'keys' для итерации", source.Span);
				element = new BoundErrorExpression(source.Span);
				elementType = ErrorType.Instance;
				condition = element;
				increment = EmptyLoopBlock(source.Span);
				return false;
			}

			var keyType = dictionary.Arguments[0];
			var valueType = dictionary.Arguments[1];
			var pairType = new TupleType(
			[
				new TupleElement(keyType, null),
				new TupleElement(valueType, null)
			]);

			var dictLocal = DeclareLocal($"#iter{_nextSlot}", iterable.Type, false, source.Span);
			DeclareFlow(dictLocal, true, iterable.Type);
			statements.Add(new BoundLocalDeclaration(
				dictLocal.Name, dictLocal.Slot, iterable.Type, iterable, false, source.Span));
			var dictExpression = Variable(dictLocal, source.Span);

			var keyList = new BuiltinGenericType(BuiltinGenericKind.List, [keyType]);
			var keysExpression = new BoundCallExpression(keys, [], keyList, source.Span, dictExpression);
			var keysLocal = DeclareLocal($"#keys{_nextSlot}", keyList, false, source.Span);
			DeclareFlow(keysLocal, true, keyList);
			statements.Add(new BoundLocalDeclaration(
				keysLocal.Name, keysLocal.Slot, keyList, keysExpression, false, source.Span));

			var dictIndex = DeclareLocal($"#index{_nextSlot}", PrimitiveType.I32, false, source.Span);
			DeclareFlow(dictIndex, true, PrimitiveType.I32);
			statements.Add(new BoundLocalDeclaration(
				dictIndex.Name, dictIndex.Slot, PrimitiveType.I32, IntLiteral("0", source.Span), false, source.Span));

			var keysVariable = Variable(keysLocal, source.Span);
			var indexVariable = Variable(dictIndex, source.Span);
			var keyExpression = new BoundIndexExpression(keysVariable, indexVariable, keyType, source.Span);
			var valueExpression = new BoundIndexExpression(dictExpression, keyExpression, valueType, source.Span);
			element = new BoundTupleExpression([keyExpression, valueExpression], pairType, source.Span);
			elementType = pairType;
			var dictLength = new BoundCallExpression(
				LoopLengthFunction(keyList), [], PrimitiveType.I32, source.Span, keysVariable);
			condition = new BoundBinaryExpression(indexVariable, "<", dictLength, PrimitiveType.Bool, source.Span);
			increment = Increment(dictIndex, IntLiteral("1", source.Span), PrimitiveType.I32, source.Span);
			return true;
		}

		if (iterable.Type.Equals(PrimitiveType.Str))
		{
			elementType = PrimitiveType.Char;
			var textLocal = DeclareLocal($"#iter{_nextSlot}", PrimitiveType.Str, false, source.Span);
			DeclareFlow(textLocal, true, PrimitiveType.Str);
			statements.Add(new BoundLocalDeclaration(
				textLocal.Name, textLocal.Slot, PrimitiveType.Str, iterable, false, source.Span));

			var textIndex = DeclareLocal($"#index{_nextSlot}", PrimitiveType.I32, false, source.Span);
			DeclareFlow(textIndex, true, PrimitiveType.I32);
			statements.Add(new BoundLocalDeclaration(
				textIndex.Name, textIndex.Slot, PrimitiveType.I32, IntLiteral("0", source.Span), false, source.Span));

			var textExpression = Variable(textLocal, source.Span);
			var textIndexExpression = Variable(textIndex, source.Span);
			var textLength = new BoundCallExpression(
				LoopLengthFunction(PrimitiveType.Str), [], PrimitiveType.I32, source.Span, textExpression);
			element = new BoundIndexExpression(textExpression, textIndexExpression, PrimitiveType.Char, source.Span);
			condition = new BoundBinaryExpression(
				textIndexExpression, "<", textLength, PrimitiveType.Bool, source.Span);
			increment = Increment(textIndex, IntLiteral("1", source.Span), PrimitiveType.I32, source.Span);
			return true;
		}

		// spec §16: тип, реализующий iter<T> через `~fun next -> T?`, итерируется протоколом.
		if (TryFindIteratorBase(iterable.Type, out var iterator))
		{
			return TryBindIteratorProtocol(
				source,
				iterable,
				iterator,
				statements,
				out element,
				out elementType,
				out condition,
				out increment,
				out prefix);
		}

		if (iterable.Type is not BuiltinGenericType
			{ BuiltinKind: BuiltinGenericKind.List or BuiltinGenericKind.Array } collection ||
			collection.Arguments.Count != 1)
		{
			Report(
				"KRN2101",
				HasAsyncIteratorBase(iterable.Type)
					? $"Итерация по '{iterable.Type}' через async_iter<T> пока не поддержана (spec §16): используйте sync iter<T> или await next() вручную"
					: $"Итерационный loop по типу '{iterable.Type}' пока не поддержан: нужен list/array, диапазон или iter<T>",
				source.Span);
			element = new BoundErrorExpression(source.Span);
			elementType = ErrorType.Instance;
			condition = element;
			increment = EmptyLoopBlock(source.Span);
			return false;
		}

		elementType = collection.Arguments[0];
		var iterableLocal = DeclareLocal($"#iter{_nextSlot}", iterable.Type, false, source.Span);
		DeclareFlow(iterableLocal, true, iterable.Type);
		statements.Add(new BoundLocalDeclaration(
			iterableLocal.Name, iterableLocal.Slot, iterable.Type, iterable, false, source.Span));

		var indexLocal = DeclareLocal($"#index{_nextSlot}", PrimitiveType.I32, false, source.Span);
		DeclareFlow(indexLocal, true, PrimitiveType.I32);
		statements.Add(new BoundLocalDeclaration(
			indexLocal.Name,
			indexLocal.Slot,
			PrimitiveType.I32,
			IntLiteral("0", source.Span),
			false,
			source.Span));

		var length = LoopLengthFunction(iterable.Type);

		var iterableExpression = Variable(iterableLocal, source.Span);
		var indexExpression = Variable(indexLocal, source.Span);
		var limit = new BoundCallExpression(length, [], PrimitiveType.I32, source.Span, iterableExpression);
		element = new BoundIndexExpression(iterableExpression, indexExpression, elementType, source.Span);
		condition = new BoundBinaryExpression(indexExpression, "<", limit, PrimitiveType.Bool, source.Span);
		increment = Increment(indexLocal, IntLiteral("1", source.Span), PrimitiveType.I32, source.Span);
		return true;
	}


	private bool TryBindRangeIteration(
		RangeExpression range,
		List<BoundStatement> statements,
		out BoundExpression element,
		out KernType elementType,
		out BoundExpression condition,
		out BoundStatement increment)
	{
		element = new BoundErrorExpression(range.Span);
		condition = element;
		increment = EmptyLoopBlock(range.Span);
		elementType = ErrorType.Instance;

		var start = BindExpression(range.Start);
		var end = BindExpression(range.End);
		if (IsError(start.Type) || IsError(end.Type))
			return false;
		if (!IsNumeric(start.Type) || !IsNumeric(end.Type) ||
			!TryCommonNumeric(start.Type, end.Type, out var numeric))
		{
			Report(
				"KRN2102",
				$"Границы диапазона должны быть числами, получено '{start.Type}' и '{end.Type}'",
				range.Span);
			return false;
		}

		start = Convert(start, numeric, range.Start.Span);
		end = Convert(end, numeric, range.End.Span);
		var step = range.Step is null
			? Convert(IntLiteral("1", range.Span), numeric, range.Span)
			: Convert(BindExpression(range.Step), numeric, range.Step.Span);

		var indexLocal = DeclareLocal($"#index{_nextSlot}", numeric, false, range.Span);
		DeclareFlow(indexLocal, true, numeric);
		statements.Add(new BoundLocalDeclaration(
			indexLocal.Name, indexLocal.Slot, numeric, start, false, range.Span));

		var limitLocal = DeclareLocal($"#limit{_nextSlot}", numeric, false, range.Span);
		DeclareFlow(limitLocal, true, numeric);
		statements.Add(new BoundLocalDeclaration(
			limitLocal.Name, limitLocal.Slot, numeric, end, false, range.Span));

		var indexExpression = Variable(indexLocal, range.Span);
		element = indexExpression;
		elementType = numeric;
		condition = new BoundBinaryExpression(
			indexExpression,
			range.IsInclusive ? "<=" : "<",
			Variable(limitLocal, range.Span),
			PrimitiveType.Bool,
			range.Span);
		increment = Increment(indexLocal, step, numeric, range.Span);
		return true;
	}

	/// <summary>
	/// Геттер длины для итерации: нативный <c>len</c> встроенного типа; если каталог не
	/// подключён (unit-тесты binder'а) — синтетический геттер <c>i32</c>, кодогенерация
	/// сообщит KRN2201, если до неё дойдёт.
	/// </summary>
	private FunctionSymbol LoopLengthFunction(KernType type)
	{
		foreach (var function in NativeMembers(type, "len"))
			if (function.Kind == FunctionKind.Getter && function.Parameters.Count == 0)
				return function;

		return new FunctionSymbol("len", FunctionKind.Getter) { ReturnType = PrimitiveType.I32 };
	}

	private BoundStatement BindLoopStatement(LoopStatement statement)
	{
		if (statement.Source is { Kind: LoopExpressionKind.Iteration } iteration)
			return BindIterationLoop(iteration, statement.Body, statement.Span);

		var label = statement.Label ?? statement.Source?.Label;
		var body = BindLoopBodyWithFlow(null, statement.Body, label, increment: null, out var breaks);
		_flow = breaks.Count > 0 ? FlowState.Join(breaks) : FlowState.Unreachable();
		return new BoundLoopStatement(null, body, IsDoWhile: false, IsInfinite: true, statement.Span)
		{
			Label = label?.Name
		};
	}

	/// <summary>
	/// <c>stop</c> ближайшего/помеченного цикла: состояние потока добавляется в breaks
	/// целевого цикла, поэтому после <b>целевого</b> цикла код остаётся достижимым, а
	/// промежуточные циклы выхода не получают.
	/// </summary>
	private BoundStatement BindStop(StopStatement statement)
	{
		var resolved = TryResolveLoopTarget(
			statement.Target,
			statement.Target is null ? statement.Span : statement.TargetSpan,
			"stop",
			out var target,
			out var depth);
		if (resolved && _flow.Reachable)
			target!.Flow.Breaks.Add(_flow.Clone());
		_flow.MarkUnreachable();
		return new BoundStopStatement(statement.Span)
		{
			TargetDepth = resolved ? depth : 0
		};
	}

	/// <summary>
	/// <c>next</c>: внутри итерационного <c>loop … in …</c> сначала выполняется инкремент
	/// счётчика, иначе переход к условию зациклил бы цикл (spec §3). Для <c>next label</c>
	/// берётся инкремент целевого цикла.
	/// </summary>
	private BoundStatement BindNext(NextStatement statement)
	{
		var resolved = TryResolveLoopTarget(
			statement.Target,
			statement.Target is null ? statement.Span : statement.TargetSpan,
			"next",
			out var target,
			out var depth);
		_flow.MarkUnreachable();

		var next = new BoundNextStatement(statement.Span)
		{
			TargetDepth = resolved ? depth : 0
		};
		if (resolved && target!.Increment is { } increment)
		{
			return new BoundBlock(
				[increment, next],
				null,
				FallsThrough: false,
				statement.Span);
		}

		return next;
	}

	private BoundAssignment Increment(BoundVariable variable, BoundExpression step, KernType type, TextSpan span) =>
		new(
			variable.Name,
			variable.Slot,
			type,
			new BoundBinaryExpression(Variable(variable, span), "+", step, type, span),
			span);

	private static BoundVariableExpression Variable(BoundVariable variable, TextSpan span) =>
		new(variable.Name, variable.Slot, variable.Type, span);

	private BoundExpression IntLiteral(string text, TextSpan span) =>
		BindExpression(new NumberLiteralExpression(text, span));

	/// <summary>Loop-выражение: результат — <c>list</c> значений тела (spec §3).</summary>
	private BoundExpression BindLoopExpression(LoopExpression loop)
	{
		if (loop.Kind == LoopExpressionKind.Iteration)
			return BindIterationLoopExpression(loop);

		if (loop.IsAsync)
		{
			Report("KRN2109", "Итерационный/асинхронный loop будет поддержан в следующем срезе M3", loop.Span);
			return new BoundErrorExpression(loop.Span);
		}

		BoundExpression? condition = null;
		if (loop.Kind is LoopExpressionKind.Condition)
			condition = BindLoopCondition(loop.Condition, loop.Span);

		var entry = _flow.Clone();
		BoundBlock body;
		List<FlowState> breaks;
		if (loop.Kind is LoopExpressionKind.DoWhile)
		{
			body = BindLoopBodyWithFlow(null, loop.Body, loop.Label, increment: null, out breaks);
			condition = BindLoopCondition(loop.Condition, loop.Span);
			var exit = _flow.Clone();
			ApplyConditionFacts(exit, condition, whenTrue: false);
			_flow = FlowState.Join(exit, breaks);
		}
		else
		{
			body = BindLoopBodyWithFlow(condition, loop.Body, loop.Label, increment: null, out breaks);
			if (loop.Kind == LoopExpressionKind.Infinite)
			{
				_flow = breaks.Count > 0 ? FlowState.Join(breaks) : FlowState.Unreachable();
			}
			else
			{
				var exit = entry.Clone();
				if (condition is not null)
					ApplyConditionFacts(exit, condition, whenTrue: false);
				_flow = FlowState.Join(exit, breaks);
			}
		}

		var valueType = body.Value?.Type ?? PrimitiveType.Void;
		var type = new BuiltinGenericType(BuiltinGenericKind.List, [valueType]);
		return new BoundLoopExpression(
			condition,
			body,
			loop.Kind == LoopExpressionKind.DoWhile,
			loop.Kind == LoopExpressionKind.Infinite,
			type,
			loop.Span)
		{
			Label = loop.Label?.Name
		};
	}

	private BoundExpression BindLoopCondition(ExpressionNode? condition, TextSpan span)
	{
		if (condition is null)
		{
			Report("KRN2101", "У цикла отсутствует условие", span);
			return new BoundErrorExpression(span);
		}

		return BindCondition(condition, "Условие цикла");
	}

	private static BoundBlock EmptyLoopBlock(TextSpan span) =>
		new([], null, FallsThrough: true, span);

	/// <summary>
	/// Тело цикла с отдельным состоянием потока: сужения сбрасываются (обратное ребро делает
	/// их неверными), затем применяются факты истинности условия. Состояния <c>stop</c>
	/// возвращаются для слияния после цикла.
	/// </summary>
	private BoundBlock BindLoopBodyWithFlow(
		BoundExpression? condition,
		SyntaxNode body,
		LoopLabel? label,
		BoundStatement? increment,
		out List<FlowState> breaks)
	{
		var bodyEntry = _flow.Clone();
		bodyEntry.ClearNarrowing();
		if (condition is not null)
			ApplyConditionFacts(bodyEntry, condition, whenTrue: true);

		var context = PushLoopContext(label, increment);
		BoundBlock bound;
		try
		{
			_flow = bodyEntry;
			bound = BindLoopBody(body);
		}
		finally
		{
			_loopContexts.Pop();
		}

		breaks = context.Flow.Breaks;
		return bound;
	}

	/// <summary>
	/// Зарегистрировать цикл на время привязки тела. Метка должна быть уникальна в функции
	/// (spec §3, раунд 27): дубликат — KRN2106, но контекст всё равно добавляется, чтобы
	/// привязка тела продолжилась.
	/// </summary>
	private LoopContext PushLoopContext(LoopLabel? label, BoundStatement? increment)
	{
		if (label is not null && !_functionLabels.Add(label.Name))
			Report("KRN2106", $"Метка цикла '{label.Name}' уже объявлена в этой функции", label.Span);
		var context = new LoopContext(label?.Name, increment);
		_loopContexts.Push(context);
		return context;
	}

	/// <summary>
	/// Разрешить цель <c>stop</c>/<c>next</c>: без имени — ближайший цикл, с именем —
	/// ближайший объемлющий цикл с этой меткой (метки лексические). <paramref name="depth"/>
	/// — 0 для ближайшего цикла; такую же глубину использует codegen.
	/// </summary>
	private bool TryResolveLoopTarget(
		string? name,
		TextSpan span,
		string keyword,
		out LoopContext? target,
		out int depth)
	{
		target = null;
		depth = -1;
		if (_loopContexts.Count == 0)
		{
			Report("KRN2106", $"Оператор {keyword} допустим только внутри цикла", span);
			return false;
		}

		if (name is null)
		{
			target = _loopContexts.Peek();
			depth = 0;
			return true;
		}

		var index = 0;
		foreach (var context in _loopContexts)
		{
			if (string.Equals(context.Label, name, StringComparison.Ordinal))
			{
				target = context;
				depth = index;
				return true;
			}
			index++;
		}

		Report("KRN2106", $"Метка цикла '{name}' не найдена среди объемлющих циклов", span);
		return false;
	}

	/// <summary>Метки текущего цикла для stop/next: состояния на момент выхода из цикла.</summary>
	private sealed class LoopFlow
	{
		public List<FlowState> Breaks { get; } = [];
	}

	/// <summary>Объемлющий цикл: метка, инкремент итерации и состояния выхода.</summary>
	private sealed class LoopContext(string? label, BoundStatement? increment)
	{
		public string? Label { get; } = label;

		/// <summary>Инкремент счётчика итерационного <c>loop … in …</c> для <c>next</c>.</summary>
		public BoundStatement? Increment { get; } = increment;

		public LoopFlow Flow { get; } = new();
	}
}
