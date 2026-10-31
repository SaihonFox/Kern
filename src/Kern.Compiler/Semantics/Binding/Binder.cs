using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Binding.Flow;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Modules;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Binder (M3): синтаксис одного модуля -> типизированное дерево. Слайс A поддерживает
/// top-level функции, литералы, локальные переменные, присваивания, арифметику/сравнения/логику,
/// <c>if</c>, <c>ret</c> и вызовы функций. Всё остальное даёт KRN2109 и не ломает разбор файла.
/// </summary>
public sealed partial class Binder
{
	private readonly ModuleSymbol _module;
	private readonly ModuleScope _moduleScope;
	private readonly TypeResolver _resolver;
	private readonly List<Diagnostic> _diagnostics = [];
	private BoundScope _scope = null!;
	private TypeParameterScope _typeScope = TypeParameterScope.Empty;
	private FunctionSymbol? _current;
	private KernType _returnType = PrimitiveType.Void;
	private BoundVariable? _selfVariable;
	private KernType? _selfType;

	/// <summary>
	/// Тип, объект которого сейчас создаётся (Q15b, spec §8): тело или пролог его конструктора,
	/// инициализаторы его полей/свойств. Только в этом контексте можно писать <c>init</c>-члены.
	/// Вложенные функции контекст не наследуют: <see cref="BindFunction"/> сбрасывает его в null
	/// перед привязкой каждой функции (get/set/init-accessor'ы и лямбды — отдельные функции).
	/// </summary>
	private TypeSymbol? _constructionType;

	/// <summary>
	/// Тип, статический инициализатор которого сейчас привязывается (Q15b). Только здесь
	/// разрешена запись <c>!</c>-статики: её значение задаётся один раз в
	/// <c>&lt;static-init&gt;</c>. Вложенные функции контекст не наследуют: его сбрасывает
	/// <see cref="BindFunction"/>.
	/// </summary>
	private TypeSymbol? _staticInitializerType;

	private int _nextSlot;
	private FlowState _flow = new();

	/// <summary>
	/// Стек объемлющих циклов текущей функции: метка, инкремент итерации и состояния
	/// <c>stop</c>. Верхний элемент — ближайший цикл (spec §3, раунд 27).
	/// </summary>
	private readonly Stack<LoopContext> _loopContexts = [];

	/// <summary>Метки циклов, уже объявленные в текущей функции (дубликат — KRN2106).</summary>
	private readonly HashSet<string> _functionLabels = new(StringComparer.Ordinal);
	private int _noneReadDepth;

	public Binder(ModuleSymbol module, SymbolTable table)
		: this(ModuleScope.ForModule(module), table)
	{
	}

	public Binder(ModuleScope scope, SymbolTable table)
	{
		ArgumentNullException.ThrowIfNull(scope);
		ArgumentNullException.ThrowIfNull(table);
		_moduleScope = scope;
		_module = scope.Module;
		_resolver = new TypeResolver(table, scope.Module.Types, scope.FindType);
	}

	public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

	public BoundModule Bind()
	{
		// B8a: конфликты #[derive(...)] с ручными членами — до привязки функций, чтобы
		// диагностика не зависела от тел методов.
		CheckDeriveConflicts();

		var functions = new List<BoundFunction>();
		var staticInitializers = new List<(TypeSymbol Type, BoundFunction Function)>();
		foreach (var symbol in _module.Functions.Values.SelectMany(group => group))
		{
			if (symbol.Kind != FunctionKind.Function || symbol.Owner is not null || symbol.ReceiverType is not null)
				continue;
			if (symbol.Body is null)
				continue;

			functions.Add(BindFunction(symbol));
		}

		// spec §10: расширения типов (функции, свойства, пользовательские операторы).
		BindExtensions(functions);

		foreach (var type in _module.Types.Values)
		{
			foreach (var group in type.Members.Functions.Values)
				foreach (var symbol in group)
					if (symbol.Body is not null && ReferenceEquals(symbol.Owner, type))
						functions.Add(BindFunction(symbol));

			// Операторы (B5a): пока единственный вызываемый — индексатор `op []`.
			// Тело привязывается как у метода: `$` — приёмник в слоте 0.
			foreach (var @operator in type.Members.Operators)
				if (@operator.Body is not null && ReferenceEquals(@operator.Owner, type))
					functions.Add(BindFunction(@operator));

			var boundConstructors = new List<BoundFunction>(type.Members.Constructors.Count);
			foreach (var constructor in type.Members.Constructors)
				boundConstructors.Add(BindFunction(constructor));
			functions.AddRange(boundConstructors);

			// Циклы цепочек проверяются после привязки всех конструкторов типа (Q16).
			CheckConstructorChainCycles(type, boundConstructors);

			foreach (var property in type.Members.Properties)
			{
				if (!ReferenceEquals(property.Owner, type))
					continue;
				if (property.Getter is { Body: not null } getter)
					functions.Add(BindFunction(getter));
				if (property.Setter is { Body: not null } setter)
					functions.Add(BindFunction(setter));
				if (property.Init is { Body: not null } init)
					functions.Add(BindFunction(init));
			}

			if (type.Members.StaticInitializer is not null)
			{
				var staticInitializer = BindStaticInitializer(type);
				staticInitializers.Add((type, staticInitializer));
				functions.Add(staticInitializer);
			}
		}

		// Статическая инициализация модуля (Q15b): один вызов на тип, порядок — объявление.
		if (staticInitializers.Count > 0)
			functions.Add(BindModuleStaticInitializer(staticInitializers));

		_diagnostics.AddRange(_resolver.Diagnostics);
		return new BoundModule(_module, functions, _diagnostics);
	}

	private BoundFunction BindFunction(FunctionSymbol symbol)
	{
		_current = symbol;
		// Контекст создания не наследуется вложенными функциями: он принадлежит только
		// конструктору этого типа (Q15b). Сбрасываем его до выбора нового контекста.
		_constructionType = symbol is { Kind: FunctionKind.Constructor, IsStatic: false, Owner: { } constructed }
			? constructed
			: null;
		// Контекст `!`-статики тоже принадлежит только её инициализатору (Q15b).
		_staticInitializerType = null;
		_nextSlot = 0;
		_flow = new FlowState();
		_loopContexts.Clear();
		_functionLabels.Clear();
		_noneReadDepth = 0;
		_selfVariable = null;
		_selfType = null;
		_typeScope = TypeParameterScope.Empty;
		// Новый кадр: слоты и захваты считаются относительно него (см. Binder.Lambdas.cs).
		_frame = ++_nextFrame;
		_boxedByFrame[_frame] = [];
		foreach (var parameter in symbol.GenericParameters)
			_typeScope = _typeScope.Add(parameter);

		_scope = new BoundScope(parent: null);
		var parameters = new List<BoundParameter>();
		if ((symbol.Owner is { } owner && !symbol.IsStatic) ||
			(symbol.Owner is null && symbol.ReceiverType is not null && !symbol.IsStatic))
		{
			_selfType = symbol.Owner is { } ownerType ? InstanceType(ownerType) : symbol.ReceiverType;
			_selfVariable = new BoundVariable("$", _selfType!, _nextSlot++, isSingleAssignment: true)
			{
				IsParameter = true,
				Frame = _frame
			};
			_scope.TryDeclare(_selfVariable);
			_flow.MarkAssigned(_selfVariable.Slot);
			parameters.Add(new BoundParameter(
				new ParameterSymbol("$", _selfType!, null, false, false, default),
				_selfVariable.Slot));
		}

		foreach (var parameter in symbol.Parameters)
		{
			var variable = new BoundVariable(parameter.Name, parameter.Type, _nextSlot++, isSingleAssignment: true)
			{
				IsParameter = true,
				Frame = _frame
			};
			if (!_scope.TryDeclare(variable))
				Report("KRN2106", $"Параметр '{parameter.Name}' уже объявлен в этой функции", parameter.Span);
			_flow.MarkAssigned(variable.Slot);
			parameters.Add(new BoundParameter(parameter, variable.Slot));
		}

		_returnType = symbol.Kind == FunctionKind.Constructor
			? PrimitiveType.Void
			: symbol.ReturnType ?? PrimitiveType.Void;
		CheckMainSignature(symbol);

		// Цепочка конструктора видит `$` и параметры этого конструктора (spec §8, п.829).
		BoundConstructorChain? chain = symbol.Kind == FunctionKind.Constructor
			? BindConstructorChain(symbol)
			: null;

		// Родители инициализируются в корне цепочки: `[A]` = `[A()]`, `[A(x), B(y)]` — все прямые
		// родители в порядке объявления (slice A3a).
		IReadOnlyList<BoundBaseConstructorCall> baseCalls =
			symbol.Kind == FunctionKind.Constructor && chain is null
				? BindBaseConstructors(symbol)
				: [];

		// Инициализаторы полей — до тела: порядок задан объявлением (spec §8, п.827).
		List<BoundFieldInitializer> initializers = symbol.Kind == FunctionKind.Constructor
			? BindInstanceInitializers(symbol)
			: [];

		var body = symbol.Body is null
			? new BoundBlock([], null, FallsThrough: true, DeclarationSpan(symbol))
			: BindBody(symbol.Body, symbol);
		var localCount = _nextSlot;
		_current = null;
		_selfVariable = null;
		_selfType = null;
		return new BoundFunction(symbol, parameters, body, _returnType, localCount)
		{
			Initializers = initializers,
			Chain = chain,
			BaseCalls = baseCalls,
			BoxedSlots = _boxedByFrame[_frame]
		};
	}

	private BoundBlock BindBody(SyntaxNode body, FunctionSymbol symbol)
	{
		if (body is BlockStatement block)
		{
			var bound = BindBlock(block, createScope: false, valueUsed: !IsVoid(_returnType), hint: _returnType);
			// Абстрактный метод тела не имеет: отсутствие значения — не ошибка (spec §8).
			if (bound.FallsThrough && !IsVoid(_returnType) && !symbol.IsAbstract)
			{
				if (bound.Value is null)
				{
					Report("KRN2104", $"Функция '{symbol.Name}' должна вернуть значение типа '{_returnType}'", DeclarationSpan(symbol));
				}
				else
				{
					var value = Convert(bound.Value, _returnType, bound.Value.Span);
					bound = bound with { Value = value, FallsThrough = false };
				}
			}

			return bound;
		}

		if (body is ExpressionNode expression)
		{
			var bound = BindExpression(expression);
			if (IsVoid(_returnType) && !IsError(bound.Type))
				Report("KRN2102", "Функция без возвращаемого типа не может возвращать выражение", expression.Span);
			var value = Convert(bound, _returnType, expression.Span);
			return new BoundBlock([], value, FallsThrough: false, body.Span);
		}

		if (body is StatementNode statement)
		{
			var bound = BindStatement(statement);
			return new BoundBlock([bound], null, FallsThrough: true, body.Span);
		}

		Report("KRN2109", "Такое тело функции пока не поддерживается (M3-A)", body.Span);
		return new BoundBlock([], null, FallsThrough: false, body.Span);
	}

	/// <summary>Тип экземпляра для <c>$</c> и приёмника метода.</summary>
	private static KernType InstanceType(TypeSymbol owner) =>
		owner.IsValueType
			? new UnitType(owner, [], owner.IsImmutable)
			: new ClassType(owner, []);

	/// <summary>Span объявления функции; у синтезированных символов берём span первого параметра.</summary>
	private static TextSpan DeclarationSpan(FunctionSymbol symbol)
	{
		if (symbol.Declaration is { } declaration)
			return declaration.Span;
		if (symbol.Parameters.Count > 0)
			return symbol.Parameters[0].Span;
		return default;
	}

	/// <summary>
	/// Операторы блока. <paramref name="hint"/> — ожидаемый тип значения блока (результат
	/// функции): последнее выражение связывается с ним, как и <c>ret</c> (spec §3).
	/// </summary>
	private BoundBlock BindBlock(BlockStatement block, bool createScope, bool valueUsed = false, KernType? hint = null)
	{
		var previous = _scope;
		if (createScope)
			_scope = new BoundScope(previous);

		try
		{
			var statements = new List<BoundStatement>(block.Statements.Count);
			var entryReachable = _flow.Reachable;
			var warned = false;

			// Локальные функции объявляются в начале блока (hoisting, spec §3).
			BindLocalFunctions(block, statements);

			for (var index = 0; index < block.Statements.Count; index++)
			{
				var statement = block.Statements[index];
				if (statement is LocalFunctionStatement)
					continue;
				// Предупреждаем один раз — о первом операторе, до которого поток не дошёл.
				// Блок, открытый уже в недостижимой области, молчит: иначе предупреждения
				// размножаются по вложенным ветвям.
				if (entryReachable && !_flow.Reachable && !warned)
				{
					Warn("KRN2304", "Недостижимый код", statement.Span);
					warned = true;
				}

				// Значение последнего выражения блока используется наружу (возврат функции,
				// ветвь if/match, тело loop), поэтому KRN2305 для него не выдаётся.
				var consumeValue = valueUsed && index == block.Statements.Count - 1;
				statements.Add(BindStatement(statement, consumeValue, consumeValue ? hint : null));
			}

			BoundExpression? value = null;
			if (statements.Count > 0 &&
				statements[^1] is BoundExpressionStatement { Expression: var last } &&
				!IsVoid(last.Type))
				value = last;

			var fallsThrough = (statements.Count == 0 || FallsThrough(statements[^1])) && _flow.Reachable;
			return new BoundBlock(statements, value, fallsThrough, block.Span);
		}
		finally
		{
			_scope = previous;
		}
	}

	private BoundStatement BindStatement(StatementNode statement, bool consumeValue = false, KernType? hint = null) => statement switch
	{
		LocalDeclarationStatement declaration => BindLocalDeclaration(declaration),
		TupleDeclarationStatement tuple => BindTupleDeclaration(tuple),
		ExpressionStatement { Expression: LoopExpression loop } => BindLoopExpressionStatement(loop),
		ExpressionStatement expression => BindExpressionStatement(expression, consumeValue, hint),
		AssignmentStatement assignment => BindAssignment(assignment),
		ReturnStatement ret => BindReturn(ret),
		BlockStatement block => BindBlock(block, createScope: true),
		UseStatement use => BindUseStatement(use),
		LoopStatement loop => BindLoopStatement(loop),
		StopStatement stop => BindStop(stop),
		NextStatement next => BindNext(next),
		_ => UnsupportedStatement(statement)
	};

	/// <summary>
	/// Loop в позиции оператора: результат-список отбрасывается, поэтому узел становится
	/// <see cref="BoundLoopStatement"/>, а не выражением list&lt;T&gt;.
	/// </summary>
	private BoundStatement BindLoopExpressionStatement(LoopExpression loop)
	{
		var expression = BindLoopExpression(loop);
		return expression switch
		{
			BoundLoopExpression bound => new BoundLoopStatement(
				bound.Condition, bound.Body, bound.IsDoWhile, bound.IsInfinite, bound.Span)
			{
				Label = bound.Label
			},
			_ => new BoundExpressionStatement(expression, loop.Span)
		};
	}

	/// <summary>Обычное выражение-оператор; расходящееся выражение делает остаток блока недостижимым.</summary>
	private BoundStatement BindExpressionStatement(ExpressionStatement statement, bool consumeValue = false, KernType? hint = null)
	{
		var expression = BindExpression(statement.Expression, hint);
		if (_flow.Reachable && !ExpressionFallsThrough(expression))
			_flow.MarkUnreachable();
		if (!consumeValue)
			WarnDiscardedValue(expression, statement.Span);
		return new BoundExpressionStatement(expression, statement.Span);
	}

	/// <summary>Тело цикла: блок или одиночное выражение/оператор в неявном блоке.</summary>
	private BoundBlock BindLoopBody(SyntaxNode body)
	{
		if (body is BlockStatement block)
			return BindBlock(block, createScope: true, valueUsed: true);

		if (body is StatementNode statement)
		{
			var bound = BindStatement(statement);
			return new BoundBlock([bound], null, FallsThrough(bound), body.Span);
		}

		if (body is ExpressionNode expression)
		{
			var bound = BindExpression(expression);
			var expressionStatement = new BoundExpressionStatement(bound, expression.Span);
			var value = IsVoid(bound.Type) ? null : bound;
			return new BoundBlock([expressionStatement], value, ExpressionFallsThrough(bound), expression.Span);
		}

		Report("KRN2109", "Такое тело цикла пока не поддерживается (M3-C)", body.Span);
		return new BoundBlock([], null, FallsThrough: false, body.Span);
	}

	private BoundVariable DeclareLocal(string name, KernType type, bool isSingleAssignment, TextSpan span)
	{
		var variable = new BoundVariable(name, type, _nextSlot++, isSingleAssignment) { Frame = _frame };
		if (!_scope.TryDeclare(variable))
			Report("KRN2106", $"Переменная '{name}' уже объявлена в этой области видимости", span);

		return variable;
	}

	private BoundStatement BindAssignment(AssignmentStatement assignment)
	{
		// `_ = expr` — явный отказ от значения (KRN2305); `_` не переменная, поэтому
		// особая ветка идёт до поиска в области видимости.
		if (assignment.Target is IdentifierExpression { Name: "_" } discard)
			return BindDiscard(assignment, discard);

		if (assignment.Target is MemberAccessExpression member)
			return BindFieldAssignment(assignment, member);

		// `a, b = b, a` (swap) и многоцелевое присваивание: значения считаются слева
		// направо во временные локальные, затем записываются в цели (spec §3).
		if (assignment.Target is TupleExpression targets)
			return BindMultiAssignment(assignment, targets);

		if (assignment.Target is not IdentifierExpression target)
		{
			Report("KRN2106", "Целью присваивания может быть только локальная переменная (M3-A)", assignment.Target.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		var variable = _scope.Lookup(target.Name);
		if (variable is null)
		{
			// Без квалификации запись в статическое поле своего типа (M3-D3b).
			if (UnqualifiedStaticField(target.Name, target.Span) is { } staticField)
				return BindStaticStore(assignment, staticField.Field, staticField.Type, target.Span);

			// Запись в поле/свойство своего экземпляра без `$`: `_val = value * 2`.
			if (TryBindImplicitMemberStore(assignment, target.Name, target.Span) is { } implicitStore)
				return implicitStore;

			Report("KRN2103", $"Неизвестная переменная '{target.Name}'", target.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		// Запись в захваченную переменную: через ячейку замыкания (замыкание хранит
		// ссылку, изменение видно объемлющей функции).
		if (variable.Frame != _frame)
			return BindCapturedAssignment(assignment, variable);

		var value = assignment.Operator == "="
			? BindExpression(assignment.Value, variable.Type)
			: BindExpression(assignment.Value);
		if (assignment.Operator != "=")
		{
			if (_flow.Reachable && !_flow.IsAssigned(variable.Slot))
				Report("KRN2105", $"Переменная '{variable.Name}' используется до присваивания", target.Span);
			var symbol = assignment.Operator.TrimEnd('=');
			var current = new BoundVariableExpression(variable.Name, variable.Slot, CurrentTypeOf(variable), target.Span);
			var combined = BindBinaryOperator(symbol, current, value, assignment.Span);
			if (combined is BoundErrorExpression)
				return new BoundBlock([], null, FallsThrough: true, assignment.Span);
			value = combined;
		}

		var valueType = value.Type;
		value = Convert(value, variable.Type, assignment.Value.Span);
		if (variable.IsSingleAssignment && _flow.Reachable && _flow.IsMaybeAssigned(variable.Slot))
			Report("KRN2105", $"Переменная '{variable.Name}' помечена как single-assignment и не может быть переприсвоена", assignment.Span);

		_flow.MarkAssigned(variable.Slot);
		ApplyAssignmentNarrowing(variable, valueType);
		return new BoundAssignment(variable.Name, variable.Slot, variable.Type, value, assignment.Span);
	}

	private BoundStatement BindFieldAssignment(AssignmentStatement assignment, MemberAccessExpression member)
	{
		// `a.b.c = v`: цепь записи (Q13/M3-D4) — копирование unit и возврат наружу.
		if (IsNestedStoreTarget(member))
			return BindChainStore(assignment, member);

		// `T.field = v`: цель — статический член, приёмника-экземпляра нет (M3-D3b).
		if (TypeReceiverOf(member.Target) is { } staticType)
			return BindStaticMemberAssignment(assignment, staticType, member);

		if (TryBindPropertyAssignment(assignment, member) is { } propertyAssignment)
			return propertyAssignment;

		var target = BindMemberAccess(member);
		if (target is not BoundFieldExpression field)
		{
			if (target is not BoundErrorExpression)
				Report("KRN2106", "Целью присваивания может быть только поле", assignment.Target.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		// Запись в поле временного unit-значения ушла бы в копию (Q13/M3-D4).
		if (field.Receiver is { } temporary && temporary.Type is UnitType { IsImmutable: false } &&
			!IsPlaceReceiver(temporary))
		{
			Report(
				"KRN2106",
				$"Нельзя изменить поле '{field.Field.Name}' у временного unit-значения: изменение будет потеряно",
				assignment.Target.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		// Backing-поле init-свойства пишется только при создании объекта (Q15b). Проверяем
		// и простые, и составные присваивания: оба идут в это поле.
		var owner = OwnerOf(field.Receiver?.Type ?? _selfType ?? PrimitiveType.Object);
		if (InitOnlyPropertyOf(owner, field.Field) is { } initProperty)
			return BindInitFieldAssignment(assignment, field, initProperty);

		var value = assignment.Operator == "="
			? BindExpression(assignment.Value, field.Type)
			: BindExpression(assignment.Value);
		if (assignment.Operator != "=")
		{
			var symbol = assignment.Operator.TrimEnd('=');
			value = BindBinaryOperator(symbol, field, value, assignment.Span);
		}

		value = Convert(value, field.Type, assignment.Value.Span);
		if (OwnerOf(field.Receiver?.Type ?? _selfType ?? PrimitiveType.Object) is { IsImmutable: true })
			Report("KRN2105", $"Нельзя изменять поле '{field.Field.Name}' неизменяемого unit!-типа", assignment.Span);

		return new BoundFieldAssignment(field.Receiver, field.Field, field.Type, value, assignment.Span);
	}

	/// <summary>
	/// Присваивание свойству без backing-поля опускается до вызова setter'а.
	/// Auto-свойства обрабатываются как поле (<see cref="PropertySymbol.BackingField"/>).
	/// </summary>
	private BoundStatement? TryBindPropertyAssignment(AssignmentStatement assignment, MemberAccessExpression member)
	{
		if (assignment.Operator != "=")
			return null;

		var receiver = member.Target is SelfExpression self
			? BindSelf(self)
			: BindExpression(member.Target);
		if (IsError(receiver.Type))
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);

		var owner = OwnerOf(receiver.Type);
		var property = owner?.Members.FindProperty(member.Member);
		if (property is null)
			return null;

		// Статическое свойство через экземпляр — неверное направление (M3-D3b).
		if (property.IsStatic)
		{
			Report(
				"KRN2103",
				$"Статический член '{property.Name}' нельзя изменять через экземпляр; нужен тип '{owner!.Name}'",
				assignment.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		// Запись в свойство временного unit-значения ушла бы в копию (Q13/M3-D4).
		if (receiver.Type is UnitType { IsImmutable: false } && !IsPlaceReceiver(receiver))
		{
			Report(
				"KRN2106",
				$"Нельзя изменить свойство '{property.Name}' у временного unit-значения: изменение будет потеряно",
				assignment.Target.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		// init-свойство без backing-поля пишется вызовом accessor'а; правила контекста —
		// в BindInitAccessorAssignment (Q15b). Авто-свойство идёт ниже как обычное поле.
		if (property is { BackingField: null, Setter: null, Init: { Body: not null } init })
			return BindInitAccessorAssignment(assignment, receiver, property, init);

		if (property is not { BackingField: null, Setter: { } setter })
			return null;

		if (owner is { IsImmutable: true })
			Report("KRN2105", $"Свойство '{property.Name}' нельзя изменять у immutable unit!-типа", assignment.Span);

		var value = BindExpression(assignment.Value, property.Type);
		value = Convert(value, property.Type, assignment.Value.Span);
		var call = new BoundCallExpression(setter, [value], property.Type, assignment.Span, receiver);
		return new BoundExpressionStatement(call, assignment.Span);
	}

	private BoundStatement BindReturn(ReturnStatement statement)
	{
		if (statement.Expression is null)
		{
			if (!IsVoid(_returnType))
				Report("KRN2102", $"Функция должна вернуть значение типа '{_returnType}'", statement.Span);
			_flow.MarkUnreachable();
			return new BoundReturn(null, statement.Span);
		}

		var value = BindExpression(statement.Expression, _returnType);
		if (IsVoid(_returnType) && !IsError(_returnType))
			Report("KRN2102", "Функция без возвращаемого типа не может вернуть значение", statement.Span);

		value = Convert(value, _returnType, statement.Expression.Span);
		_flow.MarkUnreachable();
		return new BoundReturn(value, statement.Span);
	}

	private BoundStatement UnsupportedStatement(SyntaxNode statement)
	{
		Report("KRN2109", $"Конструкция '{Describe(statement)}' будет поддержана в следующем срезе M3", statement.Span);
		return new BoundBlock([], null, FallsThrough: true, statement.Span);
	}

	private static string Describe(SyntaxNode node) => node switch
	{
		LoopStatement => "loop",
		LocalFunctionStatement => "локальная функция",
		TupleDeclarationStatement => "деструктуризация",
		UseStatement => "локальный use",
		StopStatement => "stop",
		NextStatement => "next",
		_ => node.GetType().Name
	};

	private static bool FallsThrough(BoundStatement statement) => statement switch
	{
		BoundReturn => false,
		BoundStopStatement or BoundNextStatement => false,
		BoundLoopStatement loop => !loop.IsInfinite,
		BoundBlock block => block.FallsThrough,
		BoundExpressionStatement expression => ExpressionFallsThrough(expression.Expression),
		BoundDiscardStatement discard => ExpressionFallsThrough(discard.Value),
		_ => true
	};

	private static bool ExpressionFallsThrough(BoundExpression expression) => expression switch
	{
		BoundBlockExpression block => block.FallsThrough,
		BoundIfExpression conditional => conditional.Else is null ||
			ExpressionFallsThrough(conditional.Then) ||
			ExpressionFallsThrough(conditional.Else),
		BoundLoopExpression loop => !loop.IsInfinite,
		BoundMatchExpression match => MatchFallsThrough(match),
		// Elvis всегда проходит дальше: если слева есть значение — берётся оно, иначе решает правая ветвь.
		BoundElvisExpression => true,
		BoundReturnExpression => false,
		BoundCallExpression call => call.Type is not PrimitiveType { Primitive: PrimitiveKind.Never },
		BoundErrorExpression => true,
		_ => true
	};

	/// <summary>
	/// Match завершается без значения, только если все ветви расходятся и последняя ветвь —
	/// безусловный catch-all. Без анализа полноты (M3-E+) остальное считается проходимым.
	/// </summary>
	private static bool MatchFallsThrough(BoundMatchExpression match)
	{
		if (match.Arms.Count == 0)
			return true;

		var last = match.Arms[^1];
		var catchAll = last.Guard is null && IsCatchAllPattern(last.Pattern);
		if (!catchAll)
			return true;

		return match.Arms.Any(arm => ExpressionFallsThrough(arm.Body));
	}

	private void Report(string code, string message, TextSpan span) =>
		_diagnostics.Add(new Diagnostic(code, message, span));

	private void Warn(string code, string message, TextSpan span) =>
		_diagnostics.Add(new Diagnostic(code, message, span, DiagnosticSeverity.Warning));

}
