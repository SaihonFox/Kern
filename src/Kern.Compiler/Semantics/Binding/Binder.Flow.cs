using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Binding.Flow;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

public sealed partial class Binder
{
	/// <summary>Тип переменной в текущей точке с учётом smart-cast.</summary>
	private KernType CurrentTypeOf(BoundVariable variable) =>
		_flow.NarrowedType(variable.Slot) ?? variable.Type;

	/// <summary>Регистрирует объявление в потоке и применяет сужение от инициализатора.</summary>
	private void DeclareFlow(BoundVariable variable, bool initialized, KernType? initializerType)
	{
		_flow.Reset(variable.Slot);
		if (!initialized)
			return;

		_flow.MarkAssigned(variable.Slot);
		ApplyAssignmentNarrowing(variable, initializerType);
	}

	/// <summary>
	/// После присваивания <c>T? = значение T</c> переменная сужается до <c>T</c>,
	/// а union — до типа присвоенной альтернативы (spec §4).
	/// </summary>
	private void ApplyAssignmentNarrowing(BoundVariable variable, KernType? valueType)
	{
		_flow.ClearNarrowing(variable.Slot);
		if (valueType is null or NilType or ErrorType || IsNever(valueType))
			return;
		if (valueType is PrimitiveType { Primitive: PrimitiveKind.None })
			return;

		var declared = variable.Type;
		if (declared is NullableType nullable)
		{
			if (Conversions.TryClassify(valueType, nullable.Underlying, out _))
				_flow.Narrow(variable.Slot, valueType.Equals(nullable.Underlying) ? nullable.Underlying : valueType);
			return;
		}

		if (declared is UnionType union &&
			!valueType.Equals(declared) &&
			union.Alternatives.Any(alternative => Conversions.TryClassify(valueType, alternative, out _)))
			_flow.Narrow(variable.Slot, valueType);
	}

	/// <summary>Связывает выражение в контексте, где чтение ещё не присвоенной переменной допустимо.</summary>
	private BoundExpression BindAllowingNone(ExpressionNode expression)
	{
		_noneReadDepth++;
		try
		{
			return BindExpression(expression);
		}
		finally
		{
			_noneReadDepth--;
		}
	}

	/// <summary>Условие <c>if</c>/цикла/guard: bool либо проверка наличия значения (nullable/none).</summary>
	private BoundExpression BindCondition(ExpressionNode expression, string description)
	{
		// `none` допустим только при прямой проверке `if x` / `if !x`:
		// в `if x > 0` чтение неприсвоенной переменной остаётся ошибкой.
		var allowNone = expression is IdentifierExpression or UnaryExpression { Operator: "!", Operand: IdentifierExpression };
		var bound = allowNone ? BindAllowingNone(expression) : BindExpression(expression);
		if (IsError(bound.Type) || IsBool(bound.Type) || bound is BoundTruthinessExpression)
			return bound;

		if (CanBeTruthy(bound))
			return new BoundTruthinessExpression(bound, Negated: false, expression.Span);

		Report("KRN2102", $"{description} должно быть bool, получено '{bound.Type}'", expression.Span);
		return new BoundErrorExpression(expression.Span);
	}

	/// <summary>Может ли выражение проверяться на наличие значения (<c>if x</c>).</summary>
	private bool CanBeTruthy(BoundExpression expression)
	{
		if (CanHoldNil(expression))
			return true;

		return expression is BoundVariableExpression variable && !_flow.IsAssigned(variable.Slot);
	}

	/// <summary>
	/// Может ли значение выражения быть <c>nil</c>: сам тип nullable либо переменная
	/// объявлена nullable/union'ом с nullable-альтернативой (smart-cast сужает её до T,
	/// но проверка <c>== nil</c> и <c>if x</c> остаётся допустимой).
	/// </summary>
	private bool CanHoldNil(BoundExpression expression)
	{
		if (Conversions.IsNilAssignable(expression.Type))
			return true;

		return DeclaredTypeOf(expression) is { } declared &&
			(declared is NullableType ||
			 declared is UnionType union && union.Alternatives.Any(alternative => alternative is NullableType));
	}

	/// <summary>Объявленный тип переменной (до smart-cast); для остальных выражений — null.</summary>
	private KernType? DeclaredTypeOf(BoundExpression expression) =>
		expression is BoundVariableExpression variable ? _scope.Lookup(variable.Name)?.Type : null;

	private BoundExpression BindIf(IfExpression expression)
	{
		var condition = BindCondition(expression.Condition, "Условие if");

		var thenState = _flow.Clone();
		ApplyConditionFacts(thenState, condition, whenTrue: true);
		var elseState = _flow.Clone();
		ApplyConditionFacts(elseState, condition, whenTrue: false);

		_flow = thenState;
		var then = BindBranch(expression.Then);
		var afterThen = _flow;

		_flow = elseState;
		var otherwise = expression.Else is null ? null : BindBranch(expression.Else);
		var afterElse = _flow;

		_flow = otherwise is null
			? FlowState.Join(afterThen, elseState)
			: FlowState.Join(afterThen, afterElse);

		if (otherwise is null)
			return new BoundIfExpression(condition, then, null, PrimitiveType.Void, expression.Span);

		var type = CommonExpressionType(then.Type, otherwise.Type, expression.Span);
		return new BoundIfExpression(
			condition,
			Convert(then, type, then.Span),
			Convert(otherwise, type, otherwise.Span),
			type,
			expression.Span);
	}

	private BoundExpression BindBranch(SyntaxNode branch) => branch switch
	{
		BlockStatement block => BindBlockExpression(new BlockExpression(block, block.Span)),
		ExpressionNode expression => BindExpression(expression),
		_ => UnsupportedExpression(branch)
	};

	/// <summary>
	/// <c>&amp;&amp;</c>/<c>||</c>: правый операнд вычисляется только при известном значении
	/// левого, поэтому связывается в состоянии с фактами <c>left == true</c> (для <c>&amp;&amp;</c>)
	/// или <c>left == false</c> (для <c>||</c>). Так работают <c>x != nil &amp;&amp; x &gt; 0</c>
	/// и <c>x is T &amp;&amp; …</c> (spec §4, «Smart casts»).
	/// </summary>
	private BoundExpression BindLogical(BinaryExpression binary)
	{
		var left = BindExpression(binary.Left);
		if (IsError(left.Type))
			return new BoundErrorExpression(binary.Span);

		var entry = _flow.Clone();
		var rightState = entry.Clone();
		ApplyConditionFacts(rightState, left, whenTrue: binary.Operator == "&&");

		BoundExpression right;
		_flow = rightState;
		try
		{
			right = BindExpression(binary.Right);
		}
		finally
		{
			_flow = entry;
		}

		return BindBinaryOperator(binary.Operator, left, right, binary.Span);
	}

	// ---------------------------------------------------------------- факты условий

	private void ApplyConditionFacts(FlowState state, BoundExpression condition, bool whenTrue)
	{
		switch (condition)
		{
			case BoundTruthinessExpression truthiness:
				ApplyTruthinessFacts(state, truthiness.Operand, whenTrue != truthiness.Negated);
				break;

			case BoundTypeTestExpression test:
				ApplyTypeTestFacts(state, test.Operand, test.TestType, whenTrue != test.Negated);
				break;

			case BoundUnaryExpression { Operator: "!" } unary:
				ApplyConditionFacts(state, unary.Operand, !whenTrue);
				break;

			case BoundBinaryExpression binary:
				ApplyBinaryConditionFacts(state, binary, whenTrue);
				break;
		}
	}

	private void ApplyBinaryConditionFacts(FlowState state, BoundBinaryExpression binary, bool whenTrue)
	{
		switch (binary.Operator)
		{
			case "&&" when whenTrue:
				ApplyConditionFacts(state, binary.Left, whenTrue: true);
				ApplyConditionFacts(state, binary.Right, whenTrue: true);
				break;

			case "||" when !whenTrue:
				ApplyConditionFacts(state, binary.Left, whenTrue: false);
				ApplyConditionFacts(state, binary.Right, whenTrue: false);
				break;

			case "==":
			case "!=":
				ApplyEqualityFacts(state, binary, whenTrue);
				break;
		}
	}

	/// <summary>
	/// Факты от <c>x == nil</c>/<c>x != nil</c> и <c>x == none</c>/<c>x != none</c>.
	/// <c>nil</c> доказывает присвоенность в обеих ветвях (неприсвоенное читается как none),
	/// <c>none</c> — только в ветви «значение есть».
	/// </summary>
	private static void ApplyEqualityFacts(FlowState state, BoundBinaryExpression binary, bool whenTrue)
	{
		var rightIsMarker = binary.Right is BoundNilExpression or BoundNoneExpression;
		var leftIsMarker = binary.Left is BoundNilExpression or BoundNoneExpression;
		if (rightIsMarker == leftIsMarker)
			return;

		var marker = rightIsMarker ? binary.Right : binary.Left;
		var other = rightIsMarker ? binary.Left : binary.Right;
		if (other is not BoundVariableExpression variable)
			return;

		// `!=` при истине и `==` при лжи доказывают, что значение присутствует.
		var present = (binary.Operator == "!=") == whenTrue;

		if (marker is BoundNoneExpression)
		{
			if (present)
				state.MarkAssigned(variable.Slot);
			return;
		}

		state.MarkAssigned(variable.Slot);

		// Ветка «значение точно nil»: тип остаётся nullable, сужения нет.
		if (!present)
			return;

		// Ветка доказывает, что значение не nil.
		NarrowNonNull(state, variable, variable.Type);
	}

	private void ApplyTruthinessFacts(FlowState state, BoundExpression operand, bool positive)
	{
		if (!positive || operand is not BoundVariableExpression variable)
			return;

		state.MarkAssigned(variable.Slot);
		NarrowNonNull(state, variable, operand.Type);
	}

	/// <summary>Сужает <c>T?</c>/union после доказательства, что значение не <c>nil</c>.</summary>
	private static void NarrowNonNull(FlowState state, BoundVariableExpression variable, KernType type)
	{
		switch (type)
		{
			case NullableType nullable:
				state.Narrow(variable.Slot, nullable.Underlying);
				break;

			case UnionType union:
				var rest = union.Alternatives.Where(alternative => alternative is not NullableType).ToList();
				if (rest.Count == 1)
					state.Narrow(variable.Slot, rest[0]);
				else if (rest.Count > 1 && rest.Count != union.Alternatives.Count)
					state.Narrow(variable.Slot, UnionType.Create(rest));
				break;
		}
	}

	private void ApplyTypeTestFacts(FlowState state, BoundExpression operand, KernType test, bool positive)
	{
		if (IsError(test) || operand is not BoundVariableExpression variable)
			return;

		if (positive)
		{
			state.MarkAssigned(variable.Slot);
			var current = operand.Type is NullableType nullable ? nullable.Underlying : operand.Type;
			state.Narrow(variable.Slot, NarrowToTest(current, test));
			return;
		}

		// Отрицание доказывает «не T» только для уже присвоенного значения.
		if (!state.IsAssigned(variable.Slot))
			return;

		if (RemoveAlternative(operand.Type, test) is { } narrowed)
			state.Narrow(variable.Slot, narrowed);
	}

	/// <summary>Положительный <c>is</c> не расширяет тип: сужаем только до теста/альтернативы.</summary>
	private static KernType NarrowToTest(KernType current, KernType test)
	{
		if (current.Equals(test))
			return current;

		if (current is UnionType union &&
			union.Alternatives.Any(alternative =>
				alternative.Equals(test) || Conversions.TryClassify(test, alternative, out _)))
			return test;

		return Conversions.TryClassify(test, current, out _) ? test : current;
	}

	/// <summary>Убирает альтернативу из union; для nullable-of-union сохраняет nullable-обёртку.</summary>
	private static KernType? RemoveAlternative(KernType current, KernType test)
	{
		if (current is UnionType union)
			return NarrowAfterRemoval(union.Alternatives, test, nullable: false);

		if (current is NullableType { Underlying: UnionType underlying })
			return NarrowAfterRemoval(underlying.Alternatives, test, nullable: true);

		return null;
	}

	private static KernType? NarrowAfterRemoval(
		IReadOnlyList<KernType> alternatives,
		KernType test,
		bool nullable)
	{
		var rest = alternatives.Where(alternative => !alternative.Equals(test)).ToList();
		if (rest.Count == alternatives.Count)
			return null;

		if (rest.Count == 0)
			return null;

		var narrowed = rest.Count == 1 ? rest[0] : UnionType.Create(rest);
		return nullable ? NullableType.Create(narrowed) : narrowed;
	}

	// ---------------------------------------------------------------- is / !is

	private BoundExpression BindTypeTest(BinaryExpression binary)
	{
		var operand = BindAllowingNone(binary.Left);
		if (IsError(operand.Type))
			return new BoundErrorExpression(binary.Span);

		var test = ResolveTestType(binary.Right);
		if (test is null)
			return new BoundErrorExpression(binary.Span);

		// `!is` с заведомо несовместимым типом — законная проверка (всегда true), не ошибка:
		// сужение могло оставить конкретный тип из union.
		if (binary.Operator == "is")
			CheckPatternCompatible(operand.Type, test, binary.Right.Span, "типа");

		return new BoundTypeTestExpression(operand, test, binary.Operator == "!is", binary.Span);
	}

	/// <summary>Правая часть <c>is</c> — имя типа (включая generic), а не выражение.</summary>
	private KernType? ResolveTestType(ExpressionNode node)
	{
		switch (node)
		{
			case IdentifierExpression identifier when IsTypeName(identifier.Name):
				return _resolver.Resolve(NamedTypeSyntax.Plain(identifier.Name, identifier.Span), _typeScope);

			case GenericCallExpression { Arguments.Count: 0 } generic
				when generic.Target is IdentifierExpression target && IsTypeName(target.Name):
				return _resolver.Resolve(
					new NamedTypeSyntax(target.Name, generic.TypeArguments, [], false, false, generic.Span),
					_typeScope);
		}

		Report("KRN2109", "Справа от 'is' ожидается имя типа; паттерны is будут поддержаны в M14", node.Span);
		return null;
	}

	/// <summary>
	/// <c>x == none</c>/<c>x != none</c>: <c>none</c> — начальное состояние переменной любого типа
	/// (spec §4), поэтому сравнение допустимо для любого операнда; только на равенство.
	/// </summary>
	private BoundExpression BindNoneComparison(
		string symbol,
		BoundExpression left,
		BoundExpression right,
		TextSpan span)
	{
		if (symbol is not ("==" or "!="))
		{
			Report("KRN2101", $"Оператор '{symbol}' не применим к none", span);
			return new BoundErrorExpression(span);
		}

		return new BoundBinaryExpression(left, symbol, right, PrimitiveType.Bool, span);
	}

	private BoundExpression BindNilComparison(
		string symbol,
		BoundExpression left,
		BoundExpression right,
		TextSpan span)
	{
		if (symbol is not ("==" or "!="))
		{
			Report("KRN2101", $"Оператор '{symbol}' не применим к nil", span);
			return new BoundErrorExpression(span);
		}

		if (left.Type is NilType && right.Type is NilType)
			return new BoundBinaryExpression(left, symbol, right, PrimitiveType.Bool, span);

		var other = left.Type is NilType ? right : left;
		if (!IsError(other.Type) && !CanHoldNil(other))
		{
			Report("KRN2101", $"Сравнение с nil допустимо только для nullable-типа, получено '{other.Type}'", span);
			return new BoundErrorExpression(span);
		}

		return new BoundBinaryExpression(left, symbol, right, PrimitiveType.Bool, span);
	}
}
