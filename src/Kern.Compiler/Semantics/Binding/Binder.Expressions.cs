using Kern.Binding.Formatting;
using Kern.Compiler.Lexing;
using Kern.Compiler.Semantics.Binding.Flow;
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
	/// <summary>Встроенные функции prelude, доступные до появления std (M6).</summary>
	private static readonly IReadOnlyList<FunctionSymbol> BuiltinCatalog =
	[
		Builtin("println", PrimitiveType.Void),
		Builtin("println", PrimitiveType.Void, Parameter("value", PrimitiveType.Object)),
		Builtin("print", PrimitiveType.Void, Parameter("value", PrimitiveType.Str)),
		Builtin("panic", PrimitiveType.Never, Parameter("message", PrimitiveType.Str)),
		// Запасной вариант, если нативный каталог не подключён (unit-тесты binder'а):
		// в реальном конвейере побеждают нативы `kern.core.format` (spec §3).
		Builtin("format", PrimitiveType.Str, Parameter("value", PrimitiveType.Object)),
		Builtin("format", PrimitiveType.Str,
			Parameter("value", PrimitiveType.Object), Parameter("spec", PrimitiveType.Str))
	];

	private static FunctionSymbol Builtin(string name, KernType returnType, params ParameterSymbol[] parameters) =>
		new(name, FunctionKind.Function)
		{
			Parameters = parameters,
			ReturnType = returnType
		};

	private static ParameterSymbol Parameter(string name, KernType type) =>
		new(name, type, null, false, false, default);

	private BoundExpression BindExpression(ExpressionNode expression) => BindExpression(expression, hint: null);

	/// <summary>
	/// Связывание с ожидаемым типом (spec §3): подсказка нужна только списковому литералу,
	/// чтобы <c>list&lt;i64&gt; xs = [1, 2]</c> выводил элементы как <c>i64</c>; остальные
	/// выражения подсказку игнорируют.
	/// </summary>
	private BoundExpression BindExpression(ExpressionNode expression, KernType? hint) => expression switch
	{
		IntegerLiteralExpression literal => BindNumericLiteral(literal.Text, literal.Span),
		NumberLiteralExpression literal => BindNumericLiteral(literal.Text, literal.Span),
		StringLiteralExpression literal => BindStringLiteral(literal.Text, literal.Span),
		InterpolatedStringExpression interpolated => BindInterpolatedString(interpolated),
		CharLiteralExpression literal => BindCharLiteral(literal.Text, literal.Span),
		IdentifierExpression identifier => BindIdentifier(identifier),
		SelfExpression self => BindSelf(self),
		MemberAccessExpression member => BindMemberAccess(member),
		IndexExpression index => BindIndexExpression(index),
		UnaryExpression { Operator: "ret" } ret => BindReturnExpression(ret),
		UnaryExpression unary => BindUnary(unary),
		BinaryExpression { Operator: "is" or "!is" } typeTest => BindTypeTest(typeTest),
		BinaryExpression { Operator: "&&" or "||" } logical => BindLogical(logical),
		BinaryExpression { Operator: "?:" } elvis => BindElvis(elvis),
		BinaryExpression binary => BindBinaryOperator(binary.Operator, BindExpression(binary.Left), BindExpression(binary.Right), binary.Span),
		IfExpression conditional => BindIf(conditional),
		LoopExpression loop => BindLoopExpression(loop),
		MatchExpression match => BindMatchExpression(match),
		ParenthesizedExpression parenthesized => BindExpression(parenthesized.Expression, hint),
		BlockExpression block => BindBlockExpression(block),
		CallExpression call => BindCall(call),
		StructuredCallExpression structured => BindStructuredCall(structured),
		GenericCallExpression generic => BindGenericCall(generic),
		TupleExpression tuple => BindTupleLiteral(tuple, hint),
		CollectionExpression collection => BindCollectionLiteral(collection, hint),
		TypedCollectionExpression typed => BindTypedCollectionLiteral(typed, hint),
		DictionaryExpression dictionary => BindDictionaryLiteral(dictionary, hint),
		_ => UnsupportedExpression(expression)
	};

	private BoundExpression BindBlockExpression(BlockExpression expression)
	{
		var block = BindBlock(expression.Block, createScope: true, valueUsed: true);
		var type = block.FallsThrough
			? block.Value?.Type ?? PrimitiveType.Void
			: PrimitiveType.Never;
		return new BoundBlockExpression(block.Statements, block.Value, block.FallsThrough, type, expression.Span);
	}

	private BoundExpression BindIdentifier(IdentifierExpression identifier)
	{
		if (identifier.Name == "true" || identifier.Name == "false")
			return new BoundLiteralExpression(identifier.Name == "true", null, PrimitiveType.Bool, identifier.Span);

		if (identifier.Name == "nil")
			return new BoundNilExpression(identifier.Span);

		if (identifier.Name == "none")
			return new BoundNoneExpression(identifier.Span);

		var variable = _scope.Lookup(identifier.Name);
		if (variable is null)
		{
			// Без квалификации статическое поле видно внутри любого члена своего типа,
			// но локальные имена и параметры важнее (M3-D3b).
			if (UnqualifiedStaticField(identifier.Name, identifier.Span) is { } staticField)
				return staticField;

			Report("KRN2103", $"Неизвестное имя '{identifier.Name}'", identifier.Span);
			return new BoundErrorExpression(identifier.Span);
		}

		if (_flow.Reachable && _noneReadDepth == 0 && !_flow.IsAssigned(variable.Slot) &&
			(variable.IsSingleAssignment || variable.Type is not NullableType))
			Report("KRN2105", $"Переменная '{identifier.Name}' используется до присваивания", identifier.Span);

		return new BoundVariableExpression(variable.Name, variable.Slot, CurrentTypeOf(variable), identifier.Span);
	}

	private BoundExpression BindUnary(UnaryExpression unary)
	{
		var operand = BindExpression(unary.Operand);
		if (IsError(operand.Type))
			return new BoundErrorExpression(unary.Span);

		switch (unary.Operator)
		{
			case "+":
			case "-":
				if (!IsNumeric(operand.Type))
				{
					Report("KRN2101", $"Оператор '{unary.Operator}' требует числовой операнд, получено '{operand.Type}'", unary.Span);
					return new BoundErrorExpression(unary.Span);
				}
				return new BoundUnaryExpression(unary.Operator, operand, operand.Type, unary.Span);
			case "!":
				if (IsBool(operand.Type))
					return new BoundUnaryExpression("!", operand, PrimitiveType.Bool, unary.Span);
				if (CanBeTruthy(operand))
					return new BoundTruthinessExpression(operand, Negated: true, unary.Span);
				Report("KRN2101", $"Оператор '!' требует bool или nullable-значение, получено '{operand.Type}'", unary.Span);
				return new BoundErrorExpression(unary.Span);
			case "~":
				// Побитовое дополнение сохраняет тип операнда; bool остаётся за `!` (spec §3).
				if (IsInteger(operand.Type))
					return new BoundUnaryExpression("~", operand, operand.Type, unary.Span);
				Report("KRN2101", $"Оператор '~' требует целочисленный операнд, получено '{operand.Type}'", unary.Span);
				return new BoundErrorExpression(unary.Span);
			default:
				Report("KRN2101", $"Унарный оператор '{unary.Operator}' пока не поддержан (M3-A)", unary.Span);
				return new BoundErrorExpression(unary.Span);
		}
	}

	private BoundExpression BindBinaryOperator(string symbol, BoundExpression left, BoundExpression right, TextSpan span)
	{
		if (IsError(left.Type) || IsError(right.Type))
			return new BoundErrorExpression(span);

		switch (symbol)
		{
			case "&&":
			case "||":
				if (IsBool(left.Type) && IsBool(right.Type))
					return new BoundBinaryExpression(left, symbol, right, PrimitiveType.Bool, span);
				return Incompatible(symbol, left, right, span);

			case "==":
			case "!=":
			case "<":
			case ">":
			case "<=":
			case ">=":
				return BindComparison(symbol, left, right, span);

			case "+":
				if (left.Type.Equals(PrimitiveType.Str) && right.Type.Equals(PrimitiveType.Str))
					return new BoundBinaryExpression(left, "+", right, PrimitiveType.Str, span);
				if (TryListConcat(left, right, out var listType))
					return new BoundBinaryExpression(left, "+", right, listType, span);
				return BindArithmetic(symbol, left, right, span);

			case "*":
				if (TryRepeat(left, right, span) is { } repeat)
					return repeat;
				return BindArithmetic(symbol, left, right, span);

			case "-":
			case "/":
			case "%":
			case "**":
				return BindArithmetic(symbol, left, right, span);

			case "&":
			case "|":
			case "^":
				if (IsInteger(left.Type) && IsInteger(right.Type) &&
					TryCommonNumeric(left.Type, right.Type, out var bitwiseType))
					return new BoundBinaryExpression(
						ConvertWithConversion(left, bitwiseType, span),
						symbol,
						ConvertWithConversion(right, bitwiseType, span),
						bitwiseType,
						span);
				return Incompatible(symbol, left, right, span);

			// Сдвиг: результат — тип левого операнда, счётчик — любое целое и не расширяется
			// (маскирование по ширине левого типа делает VM, spec §3).
			case "<<":
			case ">>":
				if (IsInteger(left.Type) && IsInteger(right.Type))
					return new BoundBinaryExpression(left, symbol, right, left.Type, span);
				return Incompatible(symbol, left, right, span);

			case "in":
			case "!in":
				return BindMembership(symbol, left, right, span);

			default:
				Report("KRN2101", $"Бинарный оператор '{symbol}' пока не поддержан (M3-A)", span);
				return new BoundErrorExpression(span);
		}
	}

	private BoundExpression BindArithmetic(string symbol, BoundExpression left, BoundExpression right, TextSpan span)
	{
		if (!TryCommonNumeric(left.Type, right.Type, out var common))
			return Incompatible(symbol, left, right, span);

		return new BoundBinaryExpression(
			ConvertWithConversion(left, common, span),
			symbol,
			ConvertWithConversion(right, common, span),
			common,
			span);
	}

	private BoundExpression BindComparison(string symbol, BoundExpression left, BoundExpression right, TextSpan span)
	{
		if (left.Type is PrimitiveType { Primitive: PrimitiveKind.None } ||
			right.Type is PrimitiveType { Primitive: PrimitiveKind.None })
			return BindNoneComparison(symbol, left, right, span);

		if (left.Type is NilType || right.Type is NilType)
			return BindNilComparison(symbol, left, right, span);

		if (left.Type.Equals(right.Type))
		{
			var comparable = IsNumeric(left.Type) || IsBool(left.Type) ||
				left.Type.Equals(PrimitiveType.Str) || left.Type.Equals(PrimitiveType.Char);
			if (comparable)
				return new BoundBinaryExpression(left, symbol, right, PrimitiveType.Bool, span);

			// spec §9: `==`/`!=` для unit/unit! — структурное сравнение полей, для type —
			// идентичность ссылки; оба случая исполняет VM (KValueMath.AreEqual).
			if ((symbol is "==" or "!=") && (left.Type is UnitType or ClassType))
				return new BoundBinaryExpression(left, symbol, right, PrimitiveType.Bool, span);

			// B6b1: `<`/`<=`/`>`/`>=` для unit/type — через объявленный `op compare(...) -> i32`
			// или структурное сравнение сравнимого unit'а; иначе KRN2101 (Q24).
			if ((symbol is "<" or "<=" or ">" or ">=") &&
				TryBindOrderedComparison(symbol, left, right, span) is { } ordered)
				return ordered;
		}

		if (IsNumeric(left.Type) && IsNumeric(right.Type) && TryCommonNumeric(left.Type, right.Type, out var common))
			return new BoundBinaryExpression(
				ConvertWithConversion(left, common, span),
				symbol,
				ConvertWithConversion(right, common, span),
				PrimitiveType.Bool,
				span);

		return Incompatible(symbol, left, right, span);
	}

	private BoundExpression? TryRepeat(BoundExpression left, BoundExpression right, TextSpan span)
	{
		if (IsRepeatable(left.Type) && IsInteger(right.Type))
			return new BoundBinaryExpression(left, "*", ConvertWithConversion(right, PrimitiveType.I64, span), RepeatResult(left.Type), span);
		if (IsInteger(left.Type) && IsRepeatable(right.Type))
			return new BoundBinaryExpression(ConvertWithConversion(left, PrimitiveType.I64, span), "*", right, RepeatResult(right.Type), span);
		return null;
	}

	private static KernType RepeatResult(KernType type) =>
		type.Equals(PrimitiveType.Char) ? PrimitiveType.Str : type;

	private static bool TryListConcat(BoundExpression left, BoundExpression right, out KernType type)
	{
		type = null!;
		if (left.Type is not BuiltinGenericType list || list.BuiltinKind != BuiltinGenericKind.List)
			return false;
		if (right.Type is not BuiltinGenericType other || other.BuiltinKind != BuiltinGenericKind.List)
			return false;
		if (!list.Arguments.SequenceEqual(other.Arguments))
			return false;

		type = left.Type;
		return true;
	}

	private static bool IsRepeatable(KernType type) =>
		type.Equals(PrimitiveType.Str) || type.Equals(PrimitiveType.Char) ||
		type is BuiltinGenericType { BuiltinKind: BuiltinGenericKind.List };

	private static bool IsInteger(KernType type) =>
		type is PrimitiveType primitive && NumericInfo.TryGet(primitive.Primitive, out var info) && info.IsInteger;

	private static bool IsNumeric(KernType type) => NumericInfo.IsNumeric(type);

	private static bool IsBool(KernType type) => type.Equals(PrimitiveType.Bool);

	private static bool TryCommonNumeric(KernType left, KernType right, out KernType common)
	{
		common = null!;
		if (left.Equals(right))
		{
			common = left;
			return true;
		}

		if (left is PrimitiveType leftPrimitive && right is PrimitiveType rightPrimitive &&
			NumericInfo.TryGet(leftPrimitive.Primitive, out _) && NumericInfo.TryGet(rightPrimitive.Primitive, out _))
		{
			if (NumericRules.IsWidening(leftPrimitive.Primitive, rightPrimitive.Primitive))
			{
				common = right;
				return true;
			}

			if (NumericRules.IsWidening(rightPrimitive.Primitive, leftPrimitive.Primitive))
			{
				common = left;
				return true;
			}
		}

		return false;
	}

	private BoundExpression BindLoopExpression(LoopExpression loop)
	{
		if (loop.Kind == LoopExpressionKind.Iteration || loop.IsAsync)
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

	private KernType CommonExpressionType(KernType then, KernType otherwise, TextSpan span, string context = "if")
	{
		if (IsNever(then))
			return otherwise;
		if (IsNever(otherwise))
			return then;
		if (IsError(then) || IsError(otherwise))
			return ErrorType.Instance;
		if (then.Equals(otherwise))
			return then;
		if (TryCommonNumeric(then, otherwise, out var common))
			return common;

		Report("KRN2102", $"Ветви {context} имеют несовместимые типы '{then}' и '{otherwise}'", span);
		return ErrorType.Instance;
	}

	private BoundExpression BindCall(CallExpression call)
	{
		var arguments = new CallArgumentNode[call.Arguments.Count];
		for (var index = 0; index < call.Arguments.Count; index++)
			arguments[index] = new CallArgumentNode(null, call.Arguments[index], call.Arguments[index].Span);
		return BindCall(call.Target, arguments, [], call.Span);
	}

	private BoundExpression BindStructuredCall(StructuredCallExpression call) =>
		BindCall(call.Target, call.Arguments, call.TypeArguments, call.Span);

	private BoundExpression BindGenericCall(GenericCallExpression call)
	{
		var arguments = new CallArgumentNode[call.Arguments.Count];
		for (var index = 0; index < call.Arguments.Count; index++)
			arguments[index] = new CallArgumentNode(null, call.Arguments[index], call.Arguments[index].Span);
		return BindCall(call.Target, arguments, call.TypeArguments, call.Span);
	}

	private BoundExpression BindCall(
		ExpressionNode target,
		IReadOnlyList<CallArgumentNode> arguments,
		IReadOnlyList<TypeSyntax> typeArguments,
		TextSpan span)
	{
		if (target is MemberAccessExpression member)
		{
			// `T.method(...)`: приёмник — имя типа, а не выражение (M3-D3b). Локальная
			// переменная с тем же именем важнее типа — это решает TypeReceiverOf.
			return TypeReceiverOf(member.Target) is { } staticType
				? BindTypeQualifiedCall(staticType, member, arguments, typeArguments, span)
				: BindMemberCall(member, arguments, typeArguments, span);
		}

		if (target is not IdentifierExpression identifier)
		{
			Report("KRN2109", "Такая цель вызова пока не поддерживается (M3-D)", span);
			return new BoundErrorExpression(span);
		}

		if (_module.FindType(identifier.Name) is { } type)
			return BindConstructorCall(type, arguments, typeArguments, span);

		return BindNamedCall(identifier.Name, arguments, typeArguments, span);
	}

	private BoundExpression BindNamedCall(
		string name,
		IReadOnlyList<CallArgumentNode> arguments,
		IReadOnlyList<TypeSyntax> typeArguments,
		TextSpan span)
	{
		var candidates = new List<FunctionSymbol>(_module.GetFunctions(name));
		// Без квалификации внутри любого члена своего типа видны и статические методы
		// этого типа (spec §8: `#fun` — это `static`; M3-D3b). Остальные кандидаты не
		// выбрасываем: порядок в списке не задаёт приоритет, его решает обычный выбор
		// перегрузки (OverloadResolver). Поэтому более точная сигнатура побеждает, а
		// совпадающие сигнатуры дают KRN1402, а не «тихую» подмену модульной функции.
		if (_current?.Owner is { } owner)
			candidates.AddRange(owner.Members.GetFunctions(name).Where(function => function.IsStatic));
		if (!_module.NativeFunctions.Any(function => function.NativeSelfType is null && function.Name == name))
			candidates.AddRange(BuiltinCatalog.Where(function => function.Name == name));

		return BindTargetlessCall(candidates, name, arguments, typeArguments, span);
	}

	private BoundExpression BindMemberCall(
		MemberAccessExpression member,
		IReadOnlyList<CallArgumentNode> arguments,
		IReadOnlyList<TypeSyntax> typeArguments,
		TextSpan span)
	{
		var receiver = member.Target is SelfExpression self
			? BindSelf(self)
			: BindExpression(member.Target);
		if (IsError(receiver.Type))
			return new BoundErrorExpression(span);

		// `obj?.method(...)`: при nil результатом становится nil (spec §3).
		if (member.IsConditional && receiver.Type is NullableType nullable)
		{
			return BindConditionalAccess(receiver, nullable, span, temporary =>
				BindMemberCallOn(temporary, member with { IsConditional = false }, arguments, typeArguments, span));
		}

		return BindMemberCallOn(receiver, member, arguments, typeArguments, span);
	}

	private BoundExpression BindMemberCallOn(
		BoundExpression receiver,
		MemberAccessExpression member,
		IReadOnlyList<CallArgumentNode> arguments,
		IReadOnlyList<TypeSyntax> typeArguments,
		TextSpan span)
	{
		var owner = OwnerOf(receiver.Type);
		var candidates = new List<FunctionSymbol>();
		if (owner is not null)
		{
			var declared = owner.Members.GetFunctions(member.Member);
			// Статический метод через экземпляр — неверное направление (M3-D3b).
			if (declared.Count > 0 && declared.All(function => function.IsStatic))
			{
				Report(
					"KRN2103",
					$"Статический член '{member.Member}' нельзя вызывать через экземпляр; нужен тип '{owner.Name}'",
					member.Span);
				return new BoundErrorExpression(span);
			}

			candidates.AddRange(declared.Where(function => !function.IsStatic));
		}
		if (candidates.Count == 0)
			candidates.AddRange(NativeMembers(receiver.Type, member.Member));
		if (candidates.Count == 0)
		{
			// B6b1/B8a/B8c: протоколы hash/compare/clone идут после обычных и нативных членов, но до KRN2103.
			if (TryBindProtocolMember(receiver, member.Member, arguments, span) is { } protocol)
				return protocol;
			if (owner is null)
				Report("KRN2103", $"У типа '{receiver.Type}' нет методов", member.Span);
			else
				Report("KRN2103", $"Неизвестный метод '{member.Member}' у типа '{receiver.Type}'", member.Span);
			return new BoundErrorExpression(span);
		}

		var (boundArguments, callArguments, byCallArgument) = BindArguments(arguments);
		IReadOnlyList<KernType>? explicitTypes = null;
		if (typeArguments.Count > 0)
			explicitTypes = _resolver.ResolveAll(typeArguments, _typeScope);

		var call = new CallArguments(callArguments, receiver.Type, explicitTypes, span, member.Member);
		var resolution = OverloadResolver.Resolve(candidates, call);
		if (!resolution.Success)
		{
			_diagnostics.AddRange(resolution.Diagnostics);
			return new BoundErrorExpression(span);
		}

		var ordered = new List<BoundExpression>(resolution.Arguments.Count);
		foreach (var argument in resolution.Arguments)
		{
			if (argument.Argument is null)
			{
				Report("KRN2108", $"Значения по умолчанию пока не поддержаны binder'ом: параметр '{argument.Parameter.Name}'", span);
				return new BoundErrorExpression(span);
			}

			var expression = byCallArgument[argument.Argument];
			ordered.Add(ConvertWithConversion(expression, argument.Parameter.Type, argument.Conversion, expression.Span));
		}

		WarnDeprecated(resolution.Function!, span);
		return new BoundCallExpression(
			resolution.Function!,
			ordered,
			resolution.ReturnType ?? PrimitiveType.Void,
			span,
			receiver);
	}

	private BoundExpression BindConstructorCall(
		TypeSymbol type,
		IReadOnlyList<CallArgumentNode> arguments,
		IReadOnlyList<TypeSyntax> typeArguments,
		TextSpan span)
	{
		var instanceType = InstanceType(type);
		var candidates = new List<FunctionSymbol>(type.Members.Constructors);
		if (candidates.Count == 0)
		{
			Report("KRN2103", $"У типа '{type.Name}' нет конструктора", span);
			return new BoundErrorExpression(span);
		}

		var (_, callArguments, byCallArgument) = BindArguments(arguments);
		IReadOnlyList<KernType>? explicitTypes = null;
		if (typeArguments.Count > 0)
			explicitTypes = _resolver.ResolveAll(typeArguments, _typeScope);

		var call = new CallArguments(callArguments, instanceType, explicitTypes, span, type.Name);
		var resolution = OverloadResolver.Resolve(candidates, call);
		if (!resolution.Success)
		{
			_diagnostics.AddRange(resolution.Diagnostics);
			return new BoundErrorExpression(span);
		}

		var ordered = new List<BoundExpression>(resolution.Arguments.Count);
		foreach (var argument in resolution.Arguments)
		{
			if (argument.Argument is null)
			{
				Report("KRN2108", $"Значения по умолчанию пока не поддержаны binder'ом: параметр '{argument.Parameter.Name}'", span);
				return new BoundErrorExpression(span);
			}

			var expression = byCallArgument[argument.Argument];
			ordered.Add(ConvertWithConversion(expression, argument.Parameter.Type, argument.Conversion, expression.Span));
		}

		WarnDeprecatedType(type, span);
		return new BoundObjectCreationExpression(type, resolution.Function!, ordered, instanceType, span);
	}

	private (
		List<BoundExpression> Bound,
		List<CallArgument> Calls,
		Dictionary<CallArgument, BoundExpression> ByCallArgument) BindArguments(
		IReadOnlyList<CallArgumentNode> arguments)
	{
		var bound = new List<BoundExpression>(arguments.Count);
		var calls = new List<CallArgument>(arguments.Count);
		var map = new Dictionary<CallArgument, BoundExpression>(ReferenceEqualityComparer.Instance);
		foreach (var argument in arguments)
		{
			var expression = BindExpression(argument.Value);
			var constant = (expression as BoundLiteralExpression)?.Numeric;
			var callArgument = argument.Name is null
				? CallArgument.Positional(expression.Type, constant, argument.Span)
				: CallArgument.Named(argument.Name, expression.Type, constant, argument.Span);
			bound.Add(expression);
			calls.Add(callArgument);
			map[callArgument] = expression;
		}

		return (bound, calls, map);
	}

	private BoundExpression BindSelf(SelfExpression self)
	{
		if (_selfVariable is null)
		{
			Report("KRN2106", "Выражение '$' допустимо только в методе или конструкторе типа", self.Span);
			return new BoundErrorExpression(self.Span);
		}

		return new BoundVariableExpression("$", _selfVariable.Slot, _selfVariable.Type, self.Span);
	}

	private BoundExpression BindMemberAccess(MemberAccessExpression member)
	{
		// `T.field`: приёмник — имя типа, а не выражение (M3-D3b).
		if (TypeReceiverOf(member.Target) is { } staticType)
			return BindStaticMemberAccess(staticType, member);

		var receiver = member.Target is SelfExpression self
			? BindSelf(self)
			: BindExpression(member.Target);
		if (IsError(receiver.Type))
			return new BoundErrorExpression(member.Span);

		// `obj?.prop`: при nil результатом становится nil (spec §3).
		if (member.IsConditional && receiver.Type is NullableType nullable)
		{
			return BindConditionalAccess(receiver, nullable, member.Span, temporary =>
				BindMemberAccessOn(temporary, member with { IsConditional = false }));
		}

		return BindMemberAccessOn(receiver, member);
	}

	private BoundExpression BindMemberAccessOn(BoundExpression receiver, MemberAccessExpression member)
	{
		// Кортеж: `t.x` — доступ к элементу по имени (spec §3).
		if (receiver.Type is TupleType tuple)
			return BindTupleItemAccess(receiver, tuple, member);

		var owner = OwnerOf(receiver.Type);
		if (owner is not null)
		{
			var field = owner.Members.FindField(member.Member);
			if (field is not null)
			{
				if (field.IsStatic)
					return StaticMemberThroughInstance(owner, member);

				return new BoundFieldExpression(receiver, field, field.Type, member.Span);
			}

			var property = owner.Members.FindProperty(member.Member);
			if (property is not null)
			{
				if (property.IsStatic)
					return StaticMemberThroughInstance(owner, member);

				if (property.BackingField is { } backing)
					return new BoundFieldExpression(receiver, backing, property.Type, member.Span);
				if (property.Getter is { } getter)
					return new BoundCallExpression(getter, [], property.Type, member.Span, receiver);
			}
		}

		foreach (FunctionSymbol native in NativeMembers(receiver.Type, member.Member))
		{
			if (native.Kind != FunctionKind.Getter || native.ReturnType is null)
				continue;
			return new BoundCallExpression(native, [], native.ReturnType, member.Span, receiver);
		}

		if (owner is null)
			Report("KRN2103", $"У типа '{receiver.Type}' нет полей и свойств", member.Span);
		else
			Report("KRN2103", $"Неизвестный член '{member.Member}' у типа '{receiver.Type}'", member.Span);
		return new BoundErrorExpression(member.Span);
	}

	/// <summary>Нативные члены встроенного типа: `str.len`, `list.add`, ...</summary>
	private IReadOnlyList<FunctionSymbol> NativeMembers(KernType receiver, string name)
	{
		if (_module.NativeFunctions.Count == 0)
			return [];

		var result = new List<FunctionSymbol>();
		foreach (FunctionSymbol function in _module.NativeFunctions)
		{
			if (function.NativeSelfType is null || function.Name != name)
				continue;
			if (NativeReceiverMatches(function.NativeSelfType, receiver))
				result.Add(function);
		}

		return result;
	}

	private static bool NativeReceiverMatches(KernType expected, KernType actual) =>
		expected switch
		{
			BuiltinGenericType expectedBuiltin when actual is BuiltinGenericType actualBuiltin =>
				expectedBuiltin.BuiltinKind == actualBuiltin.BuiltinKind,
			_ => expected.Equals(actual),
		};

	private static TypeSymbol? OwnerOf(KernType type) => type switch
	{
		UnitType unit => unit.Symbol,
		ClassType @class => @class.Symbol,
		_ => null,
	};

	private BoundExpression Incompatible(string symbol, BoundExpression left, BoundExpression right, TextSpan span)
	{
		Report("KRN2101", $"Оператор '{symbol}' не применим к '{left.Type}' и '{right.Type}'", span);
		return new BoundErrorExpression(span);
	}

	private BoundExpression UnsupportedExpression(SyntaxNode expression)
	{
		Report("KRN2109", $"Выражение '{expression.GetType().Name}' будет поддержано в следующем срезе M3", expression.Span);
		return new BoundErrorExpression(expression.Span);
	}

	private static bool IsVoid(KernType type) => type is PrimitiveType { Primitive: PrimitiveKind.Void };

	private static bool IsNever(KernType type) => type is PrimitiveType { Primitive: PrimitiveKind.Never };

	private static bool IsError(KernType type) => type is ErrorType;
}
