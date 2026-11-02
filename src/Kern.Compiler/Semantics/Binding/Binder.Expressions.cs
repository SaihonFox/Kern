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
		SuperExpression super => BindSuper(super),
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
		NonNullExpression nonNull => BindNonNull(nonNull),
		BlockExpression block => BindBlockExpression(block),
		WithExpression with => BindWith(with),
		IsPatternExpression isPattern => BindIsPattern(isPattern),
		CallExpression call => BindCall(call, hint),
		StructuredCallExpression structured => BindStructuredCall(structured),
		GenericCallExpression generic => BindGenericCall(generic, hint),
		TupleExpression tuple => BindTupleLiteral(tuple, hint),
		CollectionExpression collection => BindCollectionLiteral(collection, hint),
		TypedCollectionExpression typed => BindTypedCollectionLiteral(typed, hint),
		DictionaryExpression dictionary => BindDictionaryLiteral(dictionary, hint),
		LambdaExpression lambda => BindLambda(lambda, hint),
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

	/// <summary>
	/// Функция как значение: ссылка допустима, только если имя однозначно (ровно одна
	/// видимая не-нативная не-generic функция). Нативные функции через <c>MakeFunc</c>
	/// не вызываются, generic нужно инстанцировать явно — это обычный вызов.
	/// </summary>
	private BoundExpression? FunctionReference(string name, TextSpan span)
	{
		var candidates = VisibleFunctions(name)
			.Where(function =>
				function.NativeKey is null &&
				!function.IsAsync &&
				function.GenericParameters.Count == 0 &&
				function.Kind == FunctionKind.Function)
			.ToList();
		if (candidates.Count != 1)
			return null;

		FunctionSymbol function = candidates[0];
		var parameters = function.Parameters
			.Select(parameter => parameter.Type)
			.ToArray();
		var type = new FunctionType(parameters, CallResultType(function, function.ReturnType));
		return new BoundFunctionReferenceExpression(function, type, span);
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

			// Поле/свойство своего экземпляра без `$` (spec §8: `radius`, `_val`, `items`).
			if (TryBindImplicitMemberRead(identifier.Name, identifier.Span) is { } implicitMember)
				return implicitMember;

			// Функция как значение (`thread.spawn_with(square, 12)`, spec §6): ровно одна
			// видимая не-generic функция с этим именем.
			if (FunctionReference(identifier.Name, identifier.Span) is { } functionReference)
				return functionReference;

			// Имя примитивного типа как значение: `f64` в аргументе reflect.define_type(...).field.
			if (TypeCatalog.TryGetPrimitive(identifier.Name, out PrimitiveType primitive))
				return new BoundTypeOfExpression(primitive, identifier.Span);

			Report("KRN2103", $"Неизвестное имя '{identifier.Name}'", identifier.Span);
			return new BoundErrorExpression(identifier.Span);
		}

		return AccessVariable(variable, identifier.Span);
	}

	/// <summary>Постфиксный <c>!</c>: <c>T?</c> → <c>T</c>; для не-nullable — ошибка.</summary>
	private BoundExpression BindNonNull(NonNullExpression expression)
	{
		var operand = BindExpression(expression.Operand);
		if (IsError(operand.Type))
			return operand;
		if (operand.Type is NullableType nullable)
			return new BoundNonNullExpression(operand, nullable.Underlying, expression.Span);

		// Уже не-nullable: утверждение избыточно и ничего не меняет (примеры используют
		// `v!` и после smart cast, где тип уже сужен).
		return operand;
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
				if (operand is BoundIsPatternExpression { Bindings.Count: > 0 })
				{
					Report(
						"KRN2101",
						"Связывания is-паттерна недоступны после отрицания",
						unary.Operand.Span);
				}
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
			case "await":
				return BindAwait(operand, unary.Span);
			default:
				Report("KRN2101", $"Унарный оператор '{unary.Operator}' пока не поддержан (M3-A)", unary.Span);
				return new BoundErrorExpression(unary.Span);
		}
	}

	/// <summary>
	/// <c>await expr</c> (spec §16): только внутри <c>async fun</c>, операнд обязан быть
	/// <c>task&lt;T&gt;</c>, результат — <c>T</c>. Отдельного кода диагностики нет: формы
	/// ошибки — это существующий KRN2101.
	/// </summary>
	private BoundExpression BindAwait(BoundExpression operand, TextSpan span)
	{
		if (!_isAsyncFunction)
		{
			Report("KRN2101", "'await' допустим только внутри async-функции", span);
			return new BoundErrorExpression(span);
		}

		if (operand.Type is BuiltinGenericType { BuiltinKind: BuiltinGenericKind.Task, Arguments.Count: 1 } task)
			return new BoundAwaitExpression(operand, task.Arguments[0], span);

		Report("KRN2101", $"'await' ожидает 'task<T>', получено '{operand.Type}'", span);
		return new BoundErrorExpression(span);
	}

	private BoundExpression BindBinaryOperator(string symbol, BoundExpression left, BoundExpression right, TextSpan span)
	{
		if (IsError(left.Type) || IsError(right.Type))
			return new BoundErrorExpression(span);

		// `in`/`!in`: сначала пользовательский `op in(T)` на ПРАВОМ операнде (spec §16:
		// «a in b» — оператор b), иначе встроенные контейнеры.
		if (symbol is "in" or "!in")
		{
			if (TryBindUserOperator("in", right, left, span) is { } membershipOperator)
			{
				return symbol == "in"
					? membershipOperator
					: new BoundUnaryExpression("!", membershipOperator, PrimitiveType.Bool, span);
			}
		}

		// Пользовательские/расширяющие операторы (spec §12) — до встроенных, если объявлены.
		if (symbol is not ("&&" or "||" or "==" or "!=" or "<" or ">" or "<=" or ">=" or "?:" or "in" or "!in") &&
			TryBindUserOperator(symbol, left, right, span) is { } userOperator)
		{
			return userOperator;
		}

		switch (symbol)
		{
			case "&&":
			case "||":
				if (IsBool(left.Type) && IsBool(right.Type))
					return new BoundBinaryExpression(left, symbol, right, PrimitiveType.Bool, span);
				return Incompatible(symbol, left, right, span);

			case "==":
			case "!=":
			case "===":
			case "!==":
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
		// spec §3: `===`/`!==` сравнивают только ссылки значений `type`.
		if (symbol is "===" or "!==")
		{
			if (left.Type is ClassType && right.Type is ClassType)
				return new BoundBinaryExpression(left, symbol, right, PrimitiveType.Bool, span);
			return Incompatible(symbol, left, right, span);
		}

		if (left.Type is PrimitiveType { Primitive: PrimitiveKind.None } ||
			right.Type is PrimitiveType { Primitive: PrimitiveKind.None })
			return BindNoneComparison(symbol, left, right, span);

		if (left.Type is NilType || right.Type is NilType)
			return BindNilComparison(symbol, left, right, span);

		if (left.Type is TupleType leftTuple && right.Type is TupleType rightTuple &&
			TupleShapesEqual(leftTuple, rightTuple))
		{
			if (symbol is "==" or "!=")
				return new BoundBinaryExpression(left, symbol, right, PrimitiveType.Bool, span);
			if (symbol is "<" or "<=" or ">" or ">=" &&
				TryBindOrderedComparison(symbol, left, right, span) is { } tupleOrder)
				return tupleOrder;
		}

		// Нативные операторы (`duration`, `datetime`) важнее протокола `op compare`:
		// они объявлены через `[KernMember(Operator = …)]` и возвращают bool.
		if ((left.Type is ClassType { Symbol.Kind: Semantics.Types.SymbolKind.Native } ||
			 right.Type is ClassType { Symbol.Kind: Semantics.Types.SymbolKind.Native }) &&
			TryBindNativeOperator(symbol, left, right, span) is { } earlyNative)
		{
			return earlyNative;
		}

		if (left.Type.Equals(right.Type))
		{
			var comparable = IsNumeric(left.Type) || IsBool(left.Type) ||
				left.Type.Equals(PrimitiveType.Str) || left.Type.Equals(PrimitiveType.Char);
			if (comparable)
				return new BoundBinaryExpression(left, symbol, right, PrimitiveType.Bool, span);

			// Универсальные ограничения num/inum/fnum (spec §4/§16): `<`, `<=`, `>`, `>=`
			// допустимы для одинаковых T. Значения reified, поэтому сравнение выполняет VM:
			// ValueCompare даёт -1/0/1, затем результат сравнивается с нулём. Арифметика на T
			// этим не открывается — только сравнения.
			if (left.Type is TypeParameterType parameter &&
				HasNumericConstraint(parameter) &&
				symbol is "<" or "<=" or ">" or ">=")
			{
				return new BoundBinaryExpression(
					new BoundCompareExpression(left, right, PrimitiveType.I32, span),
					symbol,
					ZeroI32(span),
					PrimitiveType.Bool,
					span);
			}

			// spec §9: `==`/`!=` для unit/unit! — структурное сравнение полей, для type —
			// идентичность ссылки; объявленный `op ==` важнее (Q23).
			if ((symbol is "==" or "!=") && (left.Type is UnitType or ClassType))
			{
				if (TryBindEqualityOperator(symbol, left, right, span) is { } declared)
					return declared;
				return new BoundBinaryExpression(left, symbol, right, PrimitiveType.Bool, span);
			}

			// Кортежи — структурное равенство/порядок (spec §3; DECISIONS 2026-10-31).
			if (left.Type is TupleType)
			{
				if (symbol is "==" or "!=")
					return new BoundBinaryExpression(left, symbol, right, PrimitiveType.Bool, span);
				if (symbol is "<" or "<=" or ">" or ">=" &&
					TryBindOrderedComparison(symbol, left, right, span) is { } tupleOrdered)
					return tupleOrdered;
			}

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

		// `==`/`!=`/`<`/`<=`/`>`/`>=` нативного типа (`duration`, `datetime`): оператор
		// объявлен через `[KernMember(Operator = …)]` и возвращает bool (spec §12).
		if (TryBindNativeOperator(symbol, left, right, span) is { } nativeComparison)
			return nativeComparison;

		return Incompatible(symbol, left, right, span);
	}

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

	private static bool IsInteger(KernType type) =>
		type is PrimitiveType primitive && NumericInfo.TryGet(primitive.Primitive, out var info) && info.IsInteger;

	private static bool IsNumeric(KernType type) => NumericInfo.IsNumeric(type);

	/// <summary>
	/// Ограничение параметра обобщения включает <c>num</c>/<c>inum</c>/<c>fnum</c> (spec §4):
	/// только для такого <c>T</c> разрешены сравнения (значения reified, opcode <c>ValueCompare</c>).
	/// </summary>
	private static bool HasNumericConstraint(TypeParameterType parameter) =>
		parameter.Constraints.Any(constraint =>
			constraint is PrimitiveType
			{
				Primitive: PrimitiveKind.Num or PrimitiveKind.Inum or PrimitiveKind.Fnum
			});

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
		// Ok/Err в ветвях образуют result-union без явной аннотации.
		if (then is UnitType { Symbol: var thenSymbol } &&
			otherwise is UnitType { Symbol: var elseSymbol } &&
			ResultTypes.IsResultVariant(thenSymbol) &&
			ResultTypes.IsResultVariant(elseSymbol))
		{
			return UnionType.Create([then, otherwise]);
		}

		if (TryCommonNumeric(then, otherwise, out var common))
			return common;

		Report("KRN2102", $"Ветви {context} имеют несовместимые типы '{then}' и '{otherwise}'", span);
		return ErrorType.Instance;
	}

	private BoundExpression BindSelf(SelfExpression self)
	{
		if (_selfVariable is null)
		{
			Report("KRN2106", "Выражение '$' допустимо только в методе или конструкторе типа", self.Span);
			return new BoundErrorExpression(self.Span);
		}

		// `$` в лямбде — обычный захват значения приёмника (VM захватывает по значению).
		return AccessVariable(_selfVariable, self.Span);
	}

	/// <summary>
	/// <c>$[A]</c> в выражении (spec §2): выбирает предка получателя по имени. Член после
	/// точки привязывается к реализации <c>A</c> статически; вне метода типа выбора нет.
	/// </summary>
	private BoundExpression BindSuper(SuperExpression super)
	{
		if (_selfVariable is null || _selfType is null)
		{
			Report("KRN2106", "Выбор родителя '$[A]' допустим только в методе или конструкторе типа", super.Span);
			return new BoundErrorExpression(super.Span);
		}

		ClassType? parent = null;
		if (_selfType is ClassType receiver)
		{
			foreach (var link in Inheritance.Lineage(receiver))
				if (link.Symbol.Name == super.ParentName)
				{
					parent = link;
					break;
				}
		}

		if (parent is null)
		{
			Report("KRN2103", $"Неизвестный родитель '{super.ParentName}' у типа '{_selfType}'", super.Span);
			return new BoundErrorExpression(super.Span);
		}

		return new BoundSuperExpression(BindSelf(new SelfExpression(super.Span)), parent, super.Span);
	}

	private BoundExpression BindMemberAccess(MemberAccessExpression member)
	{
		// `env.os`: свойство нативного модуля (`[KernModule]`), а не члена значения.
		if (member.Target is IdentifierExpression moduleTarget)
		{
			foreach (var native in _module.NativeFunctions)
			{
				if (NativeModuleMatches(native.NativeModuleName, moduleTarget.Name) &&
					native.Name == member.Member &&
					native.Kind == FunctionKind.Getter &&
					native.ReturnType is not null)
				{
					return new BoundCallExpression(native, [], native.ReturnType, member.Span, null);
				}
			}
		}

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
			if (ReportMemberConflict(owner, member.Member, member.Span))
				return new BoundErrorExpression(member.Span);

			var field = owner.Members.FindField(member.Member);
			if (field is not null)
			{
				if (field.IsStatic)
					return StaticMemberThroughInstance(owner, member);

				var fieldType = Substitute(field.Type, TypeArgumentsMap(receiver.Type));
				return new BoundFieldExpression(receiver, field, fieldType, member.Span);
			}

			var property = owner.Members.FindProperty(member.Member);
			if (property is not null)
			{
				if (property.IsStatic)
					return StaticMemberThroughInstance(owner, member);

				var propertyType = Substitute(property.Type, TypeArgumentsMap(receiver.Type));
				if (property.BackingField is { } backing)
					return new BoundFieldExpression(receiver, backing, propertyType, member.Span);
				if (property.Getter is { } getter)
					return new BoundCallExpression(getter, [], propertyType, member.Span, receiver)
					{
						Virtual = IsVirtualCall(receiver, getter),
					};
			}
		}

		foreach (var group in _moduleScope.FindExtensionsFor(receiver.Type))
		{
			if (group.FindProperty(member.Member) is not { Getter: { } extensionGetter } extensionProperty)
				continue;

			var propertyType = extensionProperty.Type;
			if (TypePatternMatcher.TryMatch(group.ReceiverType, receiver.Type, out var extensionBindings))
				propertyType = TypeSubstitution.Apply(propertyType, extensionBindings);
			return new BoundCallExpression(extensionGetter, [], propertyType, member.Span, receiver);
		}

		foreach (FunctionSymbol native in NativeMembers(receiver.Type, member.Member))
		{
			if (native.Kind != FunctionKind.Getter || native.ReturnType is null)
				continue;
			var nativeType = native.ReturnType;
			if (receiver.Type is BuiltinGenericType builtinReceiver &&
				BuiltinMemberSignatures.ResolveReturnType(builtinReceiver, member.Member, [], out _) is { } substituted)
			{
				nativeType = substituted;
			}
			else
			{
				nativeType = SubstituteNativeReturn(nativeType, receiver.Type);
			}
			return new BoundCallExpression(native, [], nativeType, member.Span, receiver);
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

		// Точный нативный получатель важнее моста `object` (streamwriter, рефлексия):
		// иначе члены `type`/`macro_decl_info` перекрывали бы методы конкретного типа.
		if (result.Count > 1 &&
			result.Any(function => function.NativeSelfType is not PrimitiveType { Primitive: PrimitiveKind.Object }))
			result = [.. result.Where(function =>
				function.NativeSelfType is not PrimitiveType { Primitive: PrimitiveKind.Object })];

		return result;
	}

	private static bool NativeReceiverMatches(KernType expected, KernType actual)
	{
		// Мост `[KernType("object")]` обслуживает нативные handle-типы (streamwriter и т.п.):
		// их члены зарегистрированы на object, а символ типа объявлен отдельно.
		if (expected.Equals(PrimitiveType.Object) &&
			actual is ClassType { Symbol.Kind: Semantics.Types.SymbolKind.Native })
			return true;
		return NativeReceiverMatchesCore(expected, actual);
	}

	private static bool NativeReceiverMatchesCore(KernType expected, KernType actual) =>
		expected switch
		{
			BuiltinGenericType expectedBuiltin when actual is BuiltinGenericType actualBuiltin =>
				expectedBuiltin.BuiltinKind == actualBuiltin.BuiltinKind,
			// Нативный generic-тип (`event<T>`, `channel<T>`, `seq<T>`): аргументы выводит
			// OverloadResolver из типа получателя, поэтому здесь достаточно имени символа.
			// Сравнение по имени, а не по ссылке: символ типа может быть создан заново
			// каталогом и коллектором деклараций.
			ClassType { Symbol.Kind: Semantics.Types.SymbolKind.Native } expectedClass
				when actual is ClassType actualClass =>
				expectedClass.Symbol.Name == actualClass.Symbol.Name &&
				expectedClass.Arguments.Count == actualClass.Arguments.Count,
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
