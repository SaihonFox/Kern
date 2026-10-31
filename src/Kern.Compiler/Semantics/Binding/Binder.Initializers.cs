using Kern.Compiler.Semantics.Binding.Flow;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

public sealed partial class Binder
{
	/// <summary>
	/// Инициализаторы полей и автосвойств экземпляра для конструктора (spec §8, п.827).
	/// Порядок — объявление в типе; видны только <c>$</c> и параметры primary-конструктора.
	/// Вторичный конструктор с цепочкой инициализаторы не повторяет: их выполняет вызванный
	/// конструктор (п.829), поэтому у такого конструктора пролог пуст.
	/// </summary>
	private List<BoundFieldInitializer> BindInstanceInitializers(FunctionSymbol constructor)
	{
		if (constructor.Owner is not { } owner || constructor.Kind != FunctionKind.Constructor || owner.IsStatic)
			return [];

		if (constructor.Chain is not null)
			return [];

		if (owner.PrimaryConstructor is not null && !constructor.IsPrimaryConstructor)
			return [];

		var entries = InstanceInitializerEntries(owner);
		if (entries.Count == 0)
			return [];

		// Отдельная область: инициализаторы не видят локальные переменные тела, а видят `$`
		// (слот 0) и параметры primary-конструктора с их настоящими слотами.
		var scope = new BoundScope(parent: null);
		if (_scope.Lookup("$") is { } self)
			scope.TryDeclare(self);
		if (constructor.IsPrimaryConstructor)
		{
			foreach (var parameter in constructor.Parameters)
				if (_scope.Lookup(parameter.Name) is { } variable)
					scope.TryDeclare(variable);
		}

		var previousScope = _scope;
		var previousFlow = _flow;
		var previousNoneReadDepth = _noneReadDepth;
		var previousConstruction = _constructionType;
		_scope = scope;
		// Инициализаторы — часть создания объекта: init-записи здесь разрешены (Q15b).
		_constructionType = owner;
		// Привязка идёт в копию: состояние вызывающего кода не мутируется и не подменяется.
		_flow = previousFlow.Clone();
		try
		{
			var result = new List<BoundFieldInitializer>(entries.Count);
			foreach (var (field, expression) in entries)
			{
				var value = BindExpression(expression);
				result.Add(new BoundFieldInitializer(field, Convert(value, field.Type, expression.Span)));
			}

			return result;
		}
		finally
		{
			_scope = previousScope;
			_flow = previousFlow;
			_noneReadDepth = previousNoneReadDepth;
			_constructionType = previousConstruction;
		}
	}

	/// <summary>
	/// Пары «поле — инициализатор» в порядке объявления. Backing-поле автосвойства заменяет
	/// само свойство: пролог пишет значение прямо в слот поля.
	/// </summary>
	private static List<(FieldSymbol Field, ExpressionNode Expression)> InstanceInitializerEntries(TypeSymbol owner) =>
		InitializerEntries(owner, isStatic: false);

	/// <summary>
	/// То же для статики (Q15b): инициализаторы статических полей и backing-полей статических
	/// автосвойств в порядке объявления. Порядок общий для обоих видов: сортировка по началу
	/// выражения, иначе поле и свойство перепутались бы (они лежат в разных списках членов).
	/// </summary>
	private static List<(FieldSymbol Field, ExpressionNode Expression)> StaticInitializerEntries(TypeSymbol owner) =>
		InitializerEntries(owner, isStatic: true);

	private static List<(FieldSymbol Field, ExpressionNode Expression)> InitializerEntries(
		TypeSymbol owner,
		bool isStatic)
	{
		var entries = new List<(FieldSymbol Field, ExpressionNode Expression, int Position)>();
		foreach (var field in owner.Members.Fields)
		{
			if (field.IsStatic != isStatic || field.Initializer is not { } initializer)
				continue;
			entries.Add((field, initializer, initializer.Span.Start));
		}

		foreach (var property in owner.Members.Properties)
		{
			if (property.IsStatic != isStatic || property.BackingField is not { } backing)
				continue;
			if (property.Initializer is not { } initializer)
				continue;
			entries.Add((backing, initializer, initializer.Span.Start));
		}

		entries.Sort((left, right) => left.Position.CompareTo(right.Position));
		return [.. entries.Select(entry => (entry.Field, entry.Expression))];
	}

	/// <summary>
	/// Статический инициализатор типа (Q15b): присваивания статическим полям и backing-полям
	/// статических автосвойств в порядке объявления. У функции нет параметров и <c>$</c>
	/// (слоты появляются только из самих выражений); <c>init</c>-записи запрещены, поэтому контекст создания
	/// (<see cref="_constructionType"/>) не открывается. Запись <c>!</c>-статики разрешает
	/// только инициализатор объявившего её типа (сравнение <see cref="_staticInitializerType"/>
	/// с <see cref="FieldSymbol.Owner"/>).
	/// </summary>
	private BoundFunction BindStaticInitializer(TypeSymbol type)
	{
		var symbol = type.Members.StaticInitializer
			?? throw new InvalidOperationException($"У типа '{type.Name}' нет статического инициализатора");
		var entries = StaticInitializerEntries(type);
		TextSpan span = type.Declaration?.Span ?? default;

		// Инициализатор привязывается внутри общего прохода, поэтому состояние привязки
		// сохраняется целиком и возвращается в finally — как в BindInstanceInitializers.
		// Сегодня BindFunction всё переинициализирует, но полагаться на это нельзя.
		var previousCurrent = _current;
		var previousScope = _scope;
		var previousFlow = _flow;
		var previousLoopDepth = _loopDepth;
		// Stack перечисляется сверху вниз: восстанавливаем обратным порядком.
		var previousLoopFlows = _loopFlows.ToArray();
		var previousNoneReadDepth = _noneReadDepth;
		var previousTypeScope = _typeScope;
		var previousConstruction = _constructionType;
		var previousNextSlot = _nextSlot;
		var previousSelfVariable = _selfVariable;
		var previousSelfType = _selfType;
		var previousStaticInitializerType = _staticInitializerType;
		var previousReturnType = _returnType;

		_current = symbol;
		_constructionType = null;
		_staticInitializerType = type;
		_nextSlot = 0;
		_loopDepth = 0;
		_flow = new FlowState();
		_loopFlows.Clear();
		_noneReadDepth = 0;
		_selfVariable = null;
		_selfType = null;
		_typeScope = TypeParameterScope.Empty;
		foreach (var parameter in type.GenericParameters)
			_typeScope = _typeScope.Add(parameter);
		_scope = new BoundScope(parent: null);
		// `ret`/блочное выражение инициализатора не должны видеть возвращаемый тип
		// предыдущего члена: у статического инициализатора результат всегда void.
		_returnType = PrimitiveType.Void;

		try
		{
			var statements = new List<BoundStatement>(entries.Count);
			foreach (var (field, expression) in entries)
			{
				// Тот же путь, что у `T.field = expr`: выражение приводится к типу поля,
				// а узел остаётся статическим присваиванием без приёмника-экземпляра.
				var value = Convert(BindExpression(expression), field.Type, expression.Span);
				statements.Add(new BoundFieldAssignment(Receiver: null, field, field.Type, value, expression.Span));
			}

			return new BoundFunction(
				symbol,
				[],
				new BoundBlock(statements, null, FallsThrough: true, span),
				PrimitiveType.Void,
				_nextSlot);
		}
		finally
		{
			_current = previousCurrent;
			_scope = previousScope;
			_flow = previousFlow;
			_loopDepth = previousLoopDepth;
			_loopFlows.Clear();
			for (var index = previousLoopFlows.Length - 1; index >= 0; index--)
				_loopFlows.Push(previousLoopFlows[index]);
			_noneReadDepth = previousNoneReadDepth;
			_typeScope = previousTypeScope;
			_constructionType = previousConstruction;
			_nextSlot = previousNextSlot;
			_selfVariable = previousSelfVariable;
			_selfType = previousSelfType;
			_staticInitializerType = previousStaticInitializerType;
			_returnType = previousReturnType;
		}
	}

	/// <summary>
	/// Модульный <c>&lt;static-init&gt;</c> (Q15b): вызовы статических инициализаторов типов
	/// в порядке объявления типов в файле. На этапе 1 модуль — это одно синтаксическое дерево,
	/// поэтому порядок объявления совпадает с порядком исходного текста; типы без объявления
	/// (<see cref="TypeSymbol.Declaration"/> = <c>null</c>) сортировались бы в конец, но на
	/// практике их быть не должно. Владельца у функции нет — codegen помечает её
	/// <c>KbcModule.StaticInit</c>, и VM выполняет её ровно один раз до точки входа.
	/// </summary>
	private static BoundFunction BindModuleStaticInitializer(
		IReadOnlyList<(TypeSymbol Type, BoundFunction Function)> initializers)
	{
		var ordered = initializers
			.OrderBy(entry => entry.Type.Declaration?.Span.Start ?? int.MaxValue)
			.ToList();
		// Вызывающий код уже проверяет список; страховка на случай будущих вызовов.
		if (ordered.Count == 0)
			throw new InvalidOperationException(
				"Модульный <static-init> не может быть пустым: нужен хотя бы один инициализатор типа");
		TextSpan span = ordered[0].Type.Declaration?.Span ?? default;
		var symbol = new FunctionSymbol("<static-init>", FunctionKind.Function)
		{
			IsStatic = true,
			ReturnType = PrimitiveType.Void
		};

		var statements = new List<BoundStatement>(ordered.Count);
		foreach (var (type, function) in ordered)
		{
			TextSpan callSpan = type.Declaration?.Span ?? span;
			var call = new BoundCallExpression(function.Symbol, [], PrimitiveType.Void, callSpan);
			statements.Add(new BoundExpressionStatement(call, callSpan));
		}

		return new BoundFunction(
			symbol,
			[],
			new BoundBlock(statements, null, FallsThrough: true, span),
			PrimitiveType.Void,
			0);
	}
}
