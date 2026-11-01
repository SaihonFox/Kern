using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;

namespace Kern.Lib.Transpile.Core;

/// <summary>
/// Понижение выражений: большинство узлов печатаются напрямую, а <c>if</c>/<c>match</c>/<c>loop</c>/
/// блок в позиции значения выносятся во временную переменную (см. <see cref="Value"/>).
/// </summary>
public abstract partial class Lowerer
{
	protected string Value(BoundExpression expression)
	{
		switch (expression)
		{
			case BoundLiteralExpression literal:
				return Literal(literal);
			case BoundVariableExpression variable:
				return variable.Name == "$" ? This : NameOfSlot(variable.Slot, variable.Name);
			case BoundSuperExpression super:
				return BaseAccess(super.ParentType);
			case BoundNilExpression:
				return NilLit;
			case BoundNoneExpression:
				return NoneLit;
			case BoundTruthinessExpression truth:
				return Truthiness(truth);
			case BoundTypeTestExpression test:
				return IsType(Value(test.Operand), test.TestType, test.Negated);
			case BoundBinaryExpression binary:
				return Binary(binary);
			case BoundUnaryExpression unary:
				return Unary(unary);
			case BoundNonNullExpression nonNull:
				return NonNullText(Value(nonNull.Operand));
			case BoundAwaitExpression await:
				return AwaitText(Value(await.Operand));
			case BoundElvisExpression elvis:
				return Elvis(elvis);
			case BoundReturnExpression ret:
				ReturnExpression(ret);
				return NoneLit;
			case BoundConditionalAccessExpression access:
				return ConditionalAccess(access);
			case BoundConversionExpression conversion:
				return ConversionValue(conversion);
			case BoundBlockExpression block:
				return BlockValue(block);
			case BoundIfExpression whenExpression:
				return HoistControl(whenExpression, dest => IfControl(whenExpression, dest));
			case BoundMatchExpression match:
				return HoistControl(match, dest => MatchControl(match, dest));
			case BoundLoopExpression loop:
				return LoopValue(loop);
			case BoundCallExpression call:
				return CallValue(call);
			case BoundIndexExpression index:
				return $"{Value(index.Target)}[{Value(index.Index)}]";
			case BoundSliceExpression slice:
				return RestSlice(Value(slice.Target), Value(slice.Start), Value(slice.End), slice.Inclusive);
			case BoundFieldExpression field:
				return FieldAccess(field);
			case BoundObjectCreationExpression creation:
				return NewText(creation.Type, ArgumentTexts(creation.Arguments));
			case BoundErrorExpression:
				return "default";
			case BoundCollectionLiteral collection:
				return Collection(collection);
			case BoundDictionaryLiteral dictionary:
				return Dictionary(dictionary);
			case BoundTupleExpression tuple:
				return TupleLit([.. tuple.Items.Select(Value)]);
			case BoundTupleItemExpression item:
				return TupleItemText(Value(item.Target), item.Index, (item.Target.Type as TupleType)?.Elements.Count ?? 2);
			case BoundLambdaExpression lambda:
				return LambdaValue(lambda);
			case BoundCaptureExpression capture:
				return CaptureName(capture.Index);
			case BoundCellReferenceExpression cell:
				return NameOfSlot(cell.Slot, "cell" + cell.Slot);
			case BoundFunctionReferenceExpression reference:
				return FunctionReference(reference);
			case BoundValueCallExpression valueCall:
				return CallText(
					Value(valueCall.Target), "", [], ArgumentTexts(valueCall.Arguments), false);
			case BoundWithExpression with:
				return WithValue(with);
			case BoundResultConstructionExpression result:
				return ResultValue(result);
			case BoundHashExpression hash:
				return HashText(Value(hash.Target), hash.Structural);
			case BoundCompareExpression compare:
				return CompareText(Value(compare.Left), Value(compare.Right), compare.Structural);
			case BoundCloneExpression clone:
				return CloneText(Value(clone.Target));
			case BoundTypeOfExpression typeOf:
				return TypeOfText(typeOf.TypeArgument);
			case BoundTypeOfValueExpression typeOfValue:
				return TypeOfValueText(Value(typeOfValue.Value));
			case BoundNameOfExpression nameOf:
				return $"\"{Naming.Sanitize(nameOf.TypeArgument.ToString())}\"";
			case BoundIsPatternExpression isPattern:
				return IsPatternValue(isPattern);
			case BoundStoreCurrentExpression:
				return NoneLit;
			default:
				Warnings.Add($"выражение {expression.GetType().Name} не поддержано транспилятором");
				return "default";
		}
	}

	private IReadOnlyList<string> ArgumentTexts(IReadOnlyList<BoundExpression> arguments) =>
		[.. arguments.Select(Value)];

	private string Literal(BoundLiteralExpression literal)
	{
		if (literal.Type is PrimitiveType primitive)
		{
			switch (primitive.Primitive)
			{
				case PrimitiveKind.Bool:
					return literal.Value is bool boolean && boolean ? "true" : "false";
				case PrimitiveKind.Char:
					return CharLiteral(literal.Value);
				case PrimitiveKind.Str:
					return $"\"{Naming.Escape(literal.Value as string ?? string.Empty, Id == "kotlin")}\"";
				case PrimitiveKind.Void:
				case PrimitiveKind.None:
					return NoneLit;
			}
		}

		if (literal.Type is NilType)
			return NilLit;
		if (literal.Numeric is not null)
			return NumberLit(literal);
		if (literal.Value is null)
			return NilLit;
		if (literal.Value is string text)
			return $"\"{Naming.Escape(text, Id == "kotlin")}\"";
		return literal.Value.ToString() ?? NoneLit;
	}

	private static string CharLiteral(object? value)
	{
		string text = value switch
		{
			char symbol => symbol.ToString(),
			string str when str.Length > 0 => str[..1],
			_ => "\0"
		};
		string body = text switch
		{
			"'" => "\\'",
			"\\" => "\\\\",
			"\n" => "\\n",
			"\r" => "\\r",
			"\t" => "\\t",
			"\0" => "\\u0000",
			_ => text
		};
		return $"'{body}'";
	}

	// ------------------------------------------------------------------ управляющие выражения

	/// <summary>Оператор (не значение): if/match/loop/блок разворачиваются, остальное — обычным путём.</summary>
	protected void Control(BoundExpression expression, Dest dest)
	{
		switch (expression)
		{
			case BoundIfExpression whenExpression:
				IfControl(whenExpression, dest);
				break;
			case BoundMatchExpression match:
				MatchControl(match, dest);
				break;
			case BoundLoopExpression loop:
				LoopControl(loop, dest);
				break;
			case BoundBlockExpression block:
				BlockControl(block, dest);
				break;
			case BoundWithExpression with:
				string text = WithValue(with);
				Finish(dest, text, with);
				break;
			case BoundReturnExpression ret:
				ReturnExpression(ret);
				break;
			case BoundErrorExpression:
				break;
			default:
				Finish(dest, Value(expression), expression);
				break;
		}
	}

	private string HoistControl(BoundExpression source, Action<Dest> emit) =>
		HoistControl(source, source.Type, emit);

	private string HoistControl(BoundExpression source, KernType type, Action<Dest> emit)
	{
		if (IsVoid(type) || IsNever(type))
		{
			emit(Dest.Discard);
			return NoneLit;
		}

		string temp = Temp();
		DeclareLocal(temp, ResultAware(type), null, false);
		emit(Dest.Assign(temp));
		return temp;
	}

	/// <summary>Вариант <c>Ok&lt;T&gt;</c>/<c>Err&lt;E&gt;</c> в позиции значения — это <c>result&lt;T,E&gt;</c>.</summary>
	private KernType ResultAware(KernType type) =>
		_expectedResult is UnionType && type is UnitType unit && ResultTypes.IsResultVariant(unit.Symbol)
			? _expectedResult
			: type;

	private string BlockValue(BoundBlockExpression block)
	{
		if (block.Value is not null && block.Statements.All(statement => RefersTo(statement, block.Value)))
			return WithResultHint(block.Type, () => Value(block.Value));
		var bound = new BoundBlock(block.Statements, block.Value, block.FallsThrough, block.Span);
		return WithResultHint(block.Type, () =>
			HoistControl(block, ResultAware(block.Type), dest => EmitBlock(bound, dest)));
	}

	private static bool RefersTo(BoundStatement statement, BoundExpression value) => statement switch
	{
		BoundExpressionStatement expression => ReferenceEquals(expression.Expression, value),
		BoundDiscardStatement discard => ReferenceEquals(discard.Value, value),
		_ => false
	};

	private void BlockControl(BoundBlockExpression block, Dest dest)
	{
		var bound = new BoundBlock(block.Statements, block.Value, block.FallsThrough, block.Span);
		WithResultHint(block.Type, () => EmitBlock(bound, dest));
	}

	private void IfControl(BoundIfExpression whenExpression, Dest dest)
	{
		W.Open($"if ({StripParens(Value(whenExpression.Condition))})");
		WithResultHint(whenExpression.Type, () => Control(whenExpression.Then, dest));
		BoundExpression? otherwise = whenExpression.Else;
		while (otherwise is BoundIfExpression nested)
		{
			var (condition, text) = Capture(() => Value(nested.Condition));
			if (text.Length != 0)
				break;
			W.CloseOpen($"else if ({StripParens(condition)})");
			WithResultHint(nested.Type, () => Control(nested.Then, dest));
			otherwise = nested.Else;
		}

		if (otherwise is not null)
		{
			W.CloseOpen("else");
			WithResultHint(whenExpression.Type, () => Control(otherwise, dest));
		}

		W.Close();
	}

	private string Elvis(BoundElvisExpression elvis)
	{
		string left = Value(elvis.Left);
		if (elvis.Right is BoundReturnExpression or BoundErrorExpression || IsNever(elvis.Right.Type))
		{
			string temp = HoistIfNeeded(left, elvis.Left.Type);
			W.Open($"if ({temp} == null)");
			ReturnExpression(elvis.Right as BoundReturnExpression);
			W.Close();
			return temp;
		}

		var (right, text) = Capture(() => WithResultHint(elvis.Type, () => Value(elvis.Right)));
		if (text.Length == 0)
			return CoalesceText(left, right);

		string subject = HoistIfNeeded(left, elvis.Left.Type);
		string result = Temp();
		DeclareLocal(result, elvis.Type, subject, false);
		W.Open($"if ({result} == null)");
		W.Line(text.TrimEnd('\n'));
		W.Line($"{result} = {StripParens(right)}{End}");
		W.Close();
		return result;
	}

	private string HoistIfNeeded(string value, KernType type)
	{
		if (IsSimple(value))
			return value;
		string temp = Temp();
		DeclareLocal(temp, type, StripParens(value), true);
		return temp;
	}

	private void ReturnExpression(BoundReturnExpression? ret)
	{
		if (ret?.Value is null)
		{
			W.Line("return" + End);
			return;
		}

		W.Line($"return {StripParens(Value(ret.Value))}{End}");
	}

	private string ConditionalAccess(BoundConditionalAccessExpression access)
	{
		string receiver = HoistIfNeeded(Value(access.Receiver), access.Receiver.Type);
		if (IsVoid(access.Type))
		{
			W.Open($"if ({receiver} != null)");
			Control(access.Access, Dest.Discard);
			W.Close();
			return NoneLit;
		}

		string result = Temp();
		DeclareLocal(result, access.Type, null, false);
		W.Open($"if ({receiver} != null)");
		W.Line($"{result} = {StripParens(Value(access.Access))}{End}");
		W.Close();
		return result;
	}

	private string ConversionValue(BoundConversionExpression conversion)
	{
		string value = WithResultHint(conversion.Type, () => Value(conversion.Operand));
		return conversion.Conversion.Kind switch
		{
			ConversionKind.Identity => value,
			ConversionKind.NilToNullable => NilLit,
			_ => ConvertText(value, conversion.Operand.Type, conversion.Type)
		};
	}

	private string Unary(BoundUnaryExpression unary) => unary.Operator switch
	{
		"-" => $"(-{StripParens(Value(unary.Operand))})",
		"+" => Value(unary.Operand),
		"~" => $"(~{StripParens(Value(unary.Operand))})",
		_ => $"(!{StripParens(Value(unary.Operand))})"
	};

	private string Binary(BoundBinaryExpression binary)
	{
		string left = Value(binary.Left);
		var (right, text) = Capture(() => Value(binary.Right));
		if (text.Length != 0 && binary.Operator is "&&" or "||")
		{
			string temp = Temp();
			DeclareLocal(temp, PrimitiveType.Bool, left, false);
			W.Open(binary.Operator == "&&" ? $"if ({temp})" : $"if (!{temp})");
			W.Line(text.TrimEnd('\n'));
			W.Line($"{temp} = {StripParens(right)}{End}");
			W.Close();
			return temp;
		}

		if (text.Length != 0)
			W.Line(text.TrimEnd('\n'));

		string l = StripParens(left);
		string r = StripParens(right);
		return binary.Operator switch
		{
			"+" => $"({l} + {r})",
			"-" => $"({l} - {r})",
			"*" => $"({l} * {r})",
			"/" => $"({l} / {r})",
			"%" => $"({l} % {r})",
			"**" => Pow(l, r),
			"==" => $"({l} == {r})",
			"!=" => $"({l} != {r})",
			"===" => RefEq(l, r, false),
			"!==" => RefEq(l, r, true),
			"<" => $"({l} < {r})",
			">" => $"({l} > {r})",
			"<=" => $"({l} <= {r})",
			">=" => $"({l} >= {r})",
			"&&" => $"({l} && {r})",
			"||" => $"({l} || {r})",
			"&" => $"({l} & {r})",
			"|" => $"({l} | {r})",
			"^" => $"({l} ^ {r})",
			"<<" => $"({l} << {r})",
			">>" => $"({l} >> {r})",
			"in" => InText(l, r, false),
			"!in" => InText(l, r, true),
			_ => $"({l} {binary.Operator} {r})"
		};
	}

	private string Truthiness(BoundTruthinessExpression truth)
	{
		string value = StripParens(Value(truth.Operand));
		string condition = $"({value} != null)";
		return truth.Negated ? $"(!{condition})" : condition;
	}

	// ------------------------------------------------------------------ доступ к членам и вызовы

	protected static string OwnerName(FunctionSymbol function)
	{
		KernType? type = function.NativeOwnerType ?? function.NativeSelfType ??
			(function.Owner is not null ? InstanceTypeOf(function.Owner) : function.ReceiverType);
		return type is null ? "" : BaseTypeName(type);
	}

	private static KernType InstanceTypeOf(TypeSymbol symbol) =>
		symbol.IsValueType ? new UnitType(symbol, [], symbol.IsImmutable) : new ClassType(symbol, []);

	private static string BaseTypeName(KernType type) => type switch
	{
		PrimitiveType primitive => primitive.Primitive switch
		{
			PrimitiveKind.I8 => "i8",
			PrimitiveKind.I16 => "i16",
			PrimitiveKind.I32 => "i32",
			PrimitiveKind.I64 => "i64",
			PrimitiveKind.U8 => "u8",
			PrimitiveKind.U16 => "u16",
			PrimitiveKind.U32 => "u32",
			PrimitiveKind.U64 => "u64",
			PrimitiveKind.F16 => "f16",
			PrimitiveKind.F32 => "f32",
			PrimitiveKind.F64 => "f64",
			PrimitiveKind.Bool => "bool",
			PrimitiveKind.Char => "char",
			PrimitiveKind.Str => "str",
			PrimitiveKind.Object => "object",
			_ => primitive.Primitive.ToString().ToLowerInvariant()
		},
		BuiltinGenericType builtin => BuiltinGenericType.GetCanonicalName(builtin.BuiltinKind),
		ClassType @class => @class.Symbol.Name,
		UnitType unit => unit.Symbol.Name,
		NullableType nullable => BaseTypeName(nullable.Underlying),
		TypeParameterType parameter => parameter.Name,
		_ => type.ToString()
	};

	private string FieldAccess(BoundFieldExpression field)
	{
		string name = FieldMemberName(field.Field);
		if (field.Field.Owner is null && field.Receiver is null)
			return GlobalRef(name);
		string qualifier = Qualifier(field.Receiver, field.Field, field.Field.IsStatic, out bool global);
		if (global)
			return GlobalRef(name);
		return qualifier.Length == 0 ? name : $"{qualifier}.{name}";
	}

	private string FunctionReference(BoundFunctionReferenceExpression reference)
	{
		FunctionSymbol function = reference.Symbol;
		string name = MemberName(function.Name);
		if (function.Owner is not null)
		{
			if (function.IsStatic)
				return FunctionRefText(null, TypeName(InstanceTypeOf(function.Owner)) + "." + name, true);
			return FunctionRefText(This, name, false);
		}

		if (function.ReceiverType is not null)
			return FunctionRefText(This, name, false);
		return FunctionRefText(null, GlobalRef(name), true);
	}

	private string CallValue(BoundCallExpression call)
	{
		FunctionSymbol function = call.Function;
		string? receiver = call.Receiver is null ? null : Value(call.Receiver);
		IReadOnlyList<string> arguments = ArgumentTexts(call.Arguments);

		if (function.IsNative)
		{
			string owner = OwnerName(function);
			string? mapped = Maps.MapCall(Id, owner, function.Name, receiver ?? "", arguments);
			if (mapped is not null)
				return mapped;

			Unmapped.Add(owner.Length == 0 ? function.Name : $"{owner}.{function.Name}");
			if (function.Kind == FunctionKind.Getter && arguments.Count == 0)
			{
				string property = MemberName(function.Name);
				return receiver is null ? property : $"{receiver}.{property}";
			}

			string nativeName = MemberName(function.Name);
			return receiver is null
				? CallText(owner.Length == 0 ? null : Naming.Pascal(owner), nativeName, call.TypeArguments, arguments, false)
				: CallText(receiver, nativeName, call.TypeArguments, arguments, false);
		}

		if (function.Kind == FunctionKind.Getter && arguments.Count == 0)
		{
			string property = MemberName(function.Name);
			if (receiver is not null)
				return $"{receiver}.{property}";
			if (function.Owner is { } getterOwner)
				return function.IsStatic
					? $"{TypeName(InstanceTypeOf(getterOwner))}.{property}"
					: $"{This}.{property}";
			return property;
		}

		if (function.Kind is (FunctionKind.Setter or FunctionKind.Init) && arguments.Count == 1)
		{
			string property = MemberName(function.Name);
			string target = receiver is not null
				? $"{receiver}.{property}"
				: function.Owner is { } setterOwner && function.IsStatic
					? $"{TypeName(InstanceTypeOf(setterOwner))}.{property}"
					: $"{This}.{property}";
			return $"{target} = {StripParens(arguments[0])}";
		}

		string name = function.IsOperator
			? OperatorName(function)
			: MemberName(function.Name);
		if (receiver is not null)
			return CallText(receiver, name, call.TypeArguments, arguments, false);
		if (function.Owner is { } declaringType)
		{
			return function.IsStatic
				? CallText(TypeName(InstanceTypeOf(declaringType)), name, call.TypeArguments, arguments, false)
				: CallText(This, name, call.TypeArguments, arguments, false);
		}

		if (function.ReceiverType is not null)
			return CallText(This, name, call.TypeArguments, arguments, false);
		return CallText(null, GlobalRef(name), call.TypeArguments, arguments, false);
	}

	protected string OperatorName(FunctionSymbol function) => function.OpSymbol switch
	{
		"+" => "OpAdd",
		"-" => "OpSub",
		"*" => "OpMul",
		"/" => "OpDiv",
		"%" => "OpRem",
		"==" => "OpEq",
		"!=" => "OpNe",
		"<" => "OpLt",
		">" => "OpGt",
		"<=" => "OpLe",
		">=" => "OpGe",
		"[]" => "OpIndex",
		_ => "Op" + MemberName(function.Name)
	};

	// ------------------------------------------------------------------ коллекции и result

	private string Collection(BoundCollectionLiteral collection)
	{
		KernType element = collection.Type is BuiltinGenericType { Arguments.Count: > 0 } generic
			? generic.Arguments[0]
			: PrimitiveType.Object;
		bool array = collection.Type is BuiltinGenericType { BuiltinKind: BuiltinGenericKind.Array };
		if (!collection.Items.Any(item => item.IsSpread))
		{
			var items = collection.Items.Select(item => Value(item.Value)).ToList();
			return array ? ArrayLit(element, items) : ListLit(element, items);
		}

		string list = Temp("list");
		DeclareLocal(list, collection.Type, NewListText(element), false);
		foreach (BoundCollectionItem item in collection.Items)
		{
			string value = Value(item.Value);
			W.Line(item.IsSpread
				? (Id == "kotlin" ? $"{list}.addAll({value})" : $"{list}.AddRange({value})")
				: AddToText(list, StripParens(value)));
		}

		return list;
	}

	private string Dictionary(BoundDictionaryLiteral dictionary)
	{
		KernType key = PrimitiveType.Object;
		KernType value = PrimitiveType.Object;
		if (dictionary.Type is BuiltinGenericType { Arguments.Count: 2 } generic)
		{
			key = generic.Arguments[0];
			value = generic.Arguments[1];
		}

		var entries = dictionary.Entries.Select(entry => (Value(entry.Key), Value(entry.Value))).ToList();
		return DictLit(key, value, entries);
	}

	private string ResultValue(BoundResultConstructionExpression result)
	{
		KernType type = _expectedResult is UnionType ? _expectedResult : result.Type;
		string payload = $"({StripParens(Value(result.Value))})";
		return ResultText(result.IsOk, payload, type);
	}

	/// <summary>Аргументы результата: <c>Ok</c>-тип и <c>Err</c>-тип (object при ошибке).</summary>
	protected static (KernType Ok, KernType Error) ResultArguments(KernType type)
	{
		KernType ok = PrimitiveType.Object;
		KernType error = PrimitiveType.Object;
		if (type is UnionType union)
		{
			foreach (KernType alternative in union.Alternatives)
			{
				if (alternative is not UnitType { Arguments.Count: 1 } unit)
					continue;
				if (unit.Symbol.Name == ResultTypes.OkName)
					ok = unit.Arguments[0];
				else if (unit.Symbol.Name == ResultTypes.ErrName)
					error = unit.Arguments[0];
			}
		}

		return (ok, error);
	}

	protected static (string Ok, string Error) ResultTypeArguments(KernType type)
	{
		(KernType ok, KernType error) = ResultArguments(type);
		return (ok.ToString(), error.ToString());
	}

	private string WithValue(BoundWithExpression with)
	{
		string subject = HoistIfNeeded(Value(with.Target), with.Target.Type);
		foreach (BoundWithAssignment assignment in with.Assignments)
		{
			string target = assignment.Field.Owner is null
				? GlobalRef(MemberName(assignment.Field.Name))
				: $"{subject}.{MemberName(assignment.Field.Name)}";
			W.Line($"{target} = {StripParens(Value(assignment.Value))}{End}");
		}

		return subject;
	}

	private string IsPatternValue(BoundIsPatternExpression isPattern)
	{
		var bindings = new List<PatternBinding>();
		string? condition = PatternCondition(isPattern.Pattern, HoistIfNeeded(Value(isPattern.Subject), isPattern.Subject.Type), bindings);
		if (bindings.Count > 0)
			Warnings.Add("связывания is-паттерна в выражении перенесены без объявления переменных");
		string result = condition ?? "true";
		return isPattern.Negated ? $"(!{result})" : result;
	}

	// ------------------------------------------------------------------ лямбды

	private string LambdaValue(BoundLambdaExpression lambda)
	{
		var captures = new Dictionary<int, string>();
		var outer = new List<(string Name, string Value)>();
		for (var index = 0; index < lambda.Captures.Count; index++)
		{
			BoundCapture capture = lambda.Captures[index];
			string value = Value(capture.Source);
			if (!IsSimple(value))
			{
				string temp = Temp("cap");
				DeclareLocal(temp, capture.Type, StripParens(value), true);
				value = temp;
			}

			captures[index] = value;
			outer.Add((capture.Name, value));
		}

		string? savedAlias = ThisAlias;
		using IDisposable scope = PushFunctionScope();
		_captureScopes.Push(captures);
		var parameters = new List<(string Name, string Type)>();
		foreach (BoundParameter parameter in lambda.Function.Parameters)
		{
			if (parameter.Symbol.Name == "$")
			{
				RegisterSlot(parameter.Slot, This);
				continue;
			}

			string name = NameOfSlot(parameter.Slot, parameter.Symbol.Name);
			parameters.Add((name, TypeName(parameter.Symbol.Type)));
		}

		CodeWriter saved = W;
		W = new CodeWriter { Level = 1 };
		try
		{
			bool returns = !IsVoid(lambda.Function.ReturnType);
			EmitBlock(lambda.Function.Body, returns ? Dest.Return : Dest.Discard);
			string block = W.ToString().TrimEnd('\n');
			_captureScopes.Pop();
			return Lambda(parameters, null, block, lambda.Function.ReturnType, lambda.Symbol.IsAsync);
		}
		finally
		{
			W = saved;
			ThisAlias = savedAlias;
			_captureScopes.Clear();
		}
	}

	protected string CaptureName(int index) =>
		_captureScopes.Count > 0 && _captureScopes.Peek().TryGetValue(index, out string? name)
			? name
			: "capture" + index;

	// ------------------------------------------------------------------ паттерны

	protected sealed record PatternBinding(string Name, int Slot, KernType Type, string Expression, bool Assign);

	private KernType? _expectedResult;

	/// <summary>Подсказка типа результата для <c>Ok(...)</c>/<c>Err(...)</c>, у которых тип — вариант, а не union.</summary>
	protected internal void WithResultHint(KernType? expected, Action body)
	{
		KernType? saved = _expectedResult;
		if (expected is UnionType union && union.Alternatives.Count == 2)
			_expectedResult = union;
		try
		{
			body();
		}
		finally
		{
			_expectedResult = saved;
		}
	}

	private string WithResultHint(KernType? expected, Func<string> body)
	{
		KernType? saved = _expectedResult;
		if (expected is UnionType union && union.Alternatives.Count == 2)
			_expectedResult = union;
		try
		{
			return body();
		}
		finally
		{
			_expectedResult = saved;
		}
	}
}
