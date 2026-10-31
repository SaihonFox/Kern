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
		if (source.Bindings.Count != 1)
		{
			Report("KRN2101", "Итерационный loop ожидает ровно одну переменную", span);
			return EmptyLoopBlock(span);
		}

		var binding = source.Bindings[0];
		var declared = binding.Type is null ? null : _resolver.Resolve(binding.Type, _typeScope);

		var previousScope = _scope;
		_scope = new BoundScope(previousScope);
		try
		{
			var statements = new List<BoundStatement>();
			if (!TryBindIteration(source, statements, out var element, out var elementType, out var condition, out var increment))
				return EmptyLoopBlock(span);

			var variable = DeclareLocal(binding.Name, declared ?? elementType, false, binding.Span);
			var loopType = declared ?? elementType;
			element = Convert(element, loopType, binding.Span);
			if (IsError(element.Type))
				return EmptyLoopBlock(span);
			DeclareFlow(variable, true, loopType);

			var afterLoop = _flow.Clone();
			afterLoop.ClearNarrowing();
			var previousIncrement = _iterationIncrement;
			BoundBlock loopBody;
			List<FlowState> breaks;
			try
			{
				_iterationIncrement = increment;
				loopBody = BindLoopBodyWithFlow(null, body, out breaks);
			}
			finally
			{
				_iterationIncrement = previousIncrement;
			}
			var bodyStatements = new List<BoundStatement>(loopBody.Statements.Count + 2)
			{
				new BoundAssignment(variable.Name, variable.Slot, loopType, element, binding.Span)
			};
			bodyStatements.AddRange(loopBody.Statements);
			bodyStatements.Add(increment);

			var block = new BoundBlock(bodyStatements, null, loopBody.FallsThrough, loopBody.Span);
			statements.Add(new BoundLoopStatement(condition, block, IsDoWhile: false, IsInfinite: false, span));
			_flow = breaks.Count > 0 ? FlowState.Join(breaks) : afterLoop;
			return new BoundBlock(statements, null, FallsThrough: true, span);
		}
		finally
		{
			_scope = previousScope;
		}
	}

	/// <summary>
	/// Итерационный loop-выражение: результат — <c>list</c> значений тела
	/// (<c>list&lt;i32&gt; squares = loop i32 i in 1..5 i ** 2</c>, spec §3).
	/// </summary>
	private BoundExpression BindIterationLoopExpression(LoopExpression loop)
	{
		if (loop.Bindings.Count != 1)
		{
			Report("KRN2101", "Итерационный loop ожидает ровно одну переменную", loop.Span);
			return new BoundErrorExpression(loop.Span);
		}

		var binding = loop.Bindings[0];
		var declared = binding.Type is null ? null : _resolver.Resolve(binding.Type, _typeScope);
		var previousScope = _scope;
		_scope = new BoundScope(previousScope);
		try
		{
			var statements = new List<BoundStatement>();
			if (!TryBindIteration(loop, statements, out var element, out var elementType, out var condition, out var increment))
				return new BoundErrorExpression(loop.Span);

			var variable = DeclareLocal(binding.Name, declared ?? elementType, false, binding.Span);
			var loopType = declared ?? elementType;
			element = Convert(element, loopType, binding.Span);
			if (IsError(element.Type))
				return new BoundErrorExpression(loop.Span);
			DeclareFlow(variable, true, loopType);

			var afterLoop = _flow.Clone();
			afterLoop.ClearNarrowing();
			var previousIncrement = _iterationIncrement;
			BoundBlock loopBody;
			List<FlowState> breaks;
			try
			{
				_iterationIncrement = increment;
				loopBody = BindLoopBodyWithFlow(null, loop.Body, out breaks);
			}
			finally
			{
				_iterationIncrement = previousIncrement;
			}

			if (loopBody.Value is null || IsVoid(loopBody.Value.Type))
			{
				Report("KRN2102", "Тело loop-выражения должно возвращать значение", loop.Span);
				return new BoundErrorExpression(loop.Span);
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

			var bodyStatements = new List<BoundStatement>(loopBody.Statements.Count + 1)
			{
				new BoundAssignment(variable.Name, variable.Slot, loopType, element, binding.Span)
			};
			bodyStatements.AddRange(loopBody.Statements);
			var bodyBlock = new BoundBlock(bodyStatements, loopBody.Value, loopBody.FallsThrough, loopBody.Span);

			var expression = new BoundLoopExpression(condition, bodyBlock, false, false, listType, loop.Span)
			{
				ListSlot = list.Slot,
				Increment = increment
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
	private bool TryBindIteration(
		LoopExpression source,
		List<BoundStatement> statements,
		out BoundExpression element,
		out KernType elementType,
		out BoundExpression condition,
		out BoundStatement increment)
	{
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

		// `dict` итерируется по ключам (list<K>), `str` — по символам.
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

			var keyList = new BuiltinGenericType(BuiltinGenericKind.List, [dictionary.Arguments[0]]);
			iterable = new BoundCallExpression(keys, [], keyList, source.Span, iterable);
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

		if (iterable.Type is not BuiltinGenericType
			{ BuiltinKind: BuiltinGenericKind.List or BuiltinGenericKind.Array } collection ||
			collection.Arguments.Count != 1)
		{
			Report(
				"KRN2101",
				$"Итерационный loop по типу '{iterable.Type}' пока не поддержан: нужен list/array или диапазон",
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

	/// <summary>
	/// <c>next</c>: внутри итерационного <c>loop … in …</c> сначала выполняется инкремент
	/// счётчика, иначе переход к условию зациклил бы цикл (spec §3).
	/// </summary>
	private BoundStatement BindNext(NextStatement statement)
	{
		if (_loopDepth == 0)
			Report("KRN2106", "Оператор next допустим только внутри цикла", statement.Span);
		_flow.MarkUnreachable();

		if (_iterationIncrement is { } increment)
		{
			return new BoundBlock(
				[increment, new BoundNextStatement(statement.Span)],
				null,
				FallsThrough: false,
				statement.Span);
		}

		return new BoundNextStatement(statement.Span);
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
			body = BindLoopBodyWithFlow(null, loop.Body, out breaks);
			condition = BindLoopCondition(loop.Condition, loop.Span);
			var exit = _flow.Clone();
			ApplyConditionFacts(exit, condition, whenTrue: false);
			_flow = FlowState.Join(exit, breaks);
		}
		else
		{
			body = BindLoopBodyWithFlow(condition, loop.Body, out breaks);
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
			loop.Span);
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
}
