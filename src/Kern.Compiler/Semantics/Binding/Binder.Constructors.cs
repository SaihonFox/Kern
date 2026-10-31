using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Overloads;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Цепочки конструкторов <c>: $(...)</c> (spec §8, п.829): выбор другого конструктора того же
/// типа обычным разрешением перегрузок и обязательность цепочки при первичном конструкторе.
/// </summary>
public sealed partial class Binder
{
	/// <summary>
	/// Привязывает цепочку текущего конструктора. Аргументы видят <c>$</c> и параметры этого
	/// конструктора; среди кандидатов — все конструкторы типа, кроме него самого.
	/// </summary>
	private BoundConstructorChain? BindConstructorChain(FunctionSymbol constructor)
	{
		if (constructor.Owner is not { } owner)
			return null;

		var chain = constructor.Chain;
		if (chain is null)
		{
			// Тип с первичным конструктором: каждый вторичный обязан вызвать другой конструктор.
			if (owner.PrimaryConstructor is not null && !constructor.IsPrimaryConstructor)
			{
				Report(
					"KRN2190",
					$"Вторичный конструктор типа '{owner.Name}' обязан вызвать другой конструктор цепочкой ': $(...)'",
					DeclarationSpan(constructor));
			}

			return null;
		}

		if (constructor.IsPrimaryConstructor)
		{
			// Синтаксис не позволяет цепочку у первичного конструктора: `: $(...)` бывает только
			// у `ctor(...)` в теле типа. Проверка страхует от символов, собранных вручную.
			Report(
				"KRN2106",
				$"Первичный конструктор типа '{owner.Name}' не может иметь цепочку ': $(...)'",
				chain.Span);
			return null;
		}

		// Цепочка вызывает другой конструктор: сам текущий из кандидатов исключён.
		var candidates = new List<FunctionSymbol>(owner.Members.Constructors.Count);
		foreach (var candidate in owner.Members.Constructors)
			if (!ReferenceEquals(candidate, constructor))
				candidates.Add(candidate);

		if (candidates.Count == 0)
		{
			Report(
				"KRN1401",
				$"Конструктор типа '{owner.Name}' не может вызывать цепочкой сам себя: другого конструктора нет",
				chain.Span);
			return null;
		}

		var argumentNodes = new CallArgumentNode[chain.Arguments.Count];
		for (var index = 0; index < chain.Arguments.Count; index++)
		{
			var argument = chain.Arguments[index];
			argumentNodes[index] = new CallArgumentNode(null, argument, argument.Span);
		}

		// Привязка идёт в копию состояния потока: цепочка не должна влиять на тело
		// (как и инициализаторы, см. BindInstanceInitializers).
		var previousFlow = _flow;
		var previousNoneReadDepth = _noneReadDepth;
		List<CallArgument> callArguments;
		Dictionary<CallArgument, BoundExpression> byCallArgument;
		_flow = previousFlow.Clone();
		try
		{
			var (_, calls, map) = BindArguments(argumentNodes);
			callArguments = calls;
			byCallArgument = map;
		}
		finally
		{
			_flow = previousFlow;
			_noneReadDepth = previousNoneReadDepth;
		}

		var call = new CallArguments(
			callArguments,
			InstanceType(owner),
			explicitTypeArguments: null,
			chain.Span,
			owner.Name);
		var resolution = OverloadResolver.Resolve(candidates, call);
		if (!resolution.Success)
		{
			_diagnostics.AddRange(resolution.Diagnostics);
			return null;
		}

		// Исключение из кандидатов делает ветку недостижимой; правило зафиксировано явно,
		// чтобы будущая правка выбора кандидатов не потеряла проверку.
		if (ReferenceEquals(resolution.Function, constructor))
		{
			Report(
				"KRN1401",
				$"Конструктор типа '{owner.Name}' не может вызывать цепочкой сам себя",
				chain.Span);
			return null;
		}

		var ordered = new List<BoundExpression>(resolution.Arguments.Count);
		foreach (var argument in resolution.Arguments)
		{
			if (argument.Argument is null)
			{
				if (BindDefaultArgument(argument.Parameter, chain.Span) is not { } defaultValue)
				{
					Report(
						"KRN2108",
						$"У параметра '{argument.Parameter.Name}' нет значения по умолчанию",
						chain.Span);
					return null;
				}

				ordered.Add(defaultValue);
				continue;
			}

			var expression = byCallArgument[argument.Argument];
			ordered.Add(ConvertWithConversion(expression, argument.Parameter.Type, argument.Conversion, expression.Span));
		}

		return new BoundConstructorChain(resolution.Function!, ordered, chain.Span);
	}

	/// <summary>
	/// Проверяет циклы в графе цепочек конструкторов (spec §8, п.829; Q16 → KRN2191).
	/// Узел — конструктор типа; ребро — успешно связанная цепочка <c>: $(...)</c>. Конструктор
	/// без цепочки — корень, из него построение завершается. Цикл, из которого корень
	/// недостижим, не может завершить построение, поэтому даёт <c>KRN2191</c>.
	/// <para>
	/// Правило диагностики зафиксировано: одна диагностика на сильно связную компоненту
	/// (цикл), представитель — конструктор, объявленный в типе последним (наибольший индекс
	/// в <c>Members.Constructors</c>), span — его цепочка <c>: $(...)</c>. Так позиция не
	/// зависит от порядка обхода графа и тесты не «плывут».
	/// </para>
	/// <para>
	/// Цепочка, которую не удалось связать (ошибка разрешения перегрузок), считается
	/// терминальной: у неё уже есть своя диагностика, каскад поверх неё не нужен.
	/// </para>
	/// </summary>
	private void CheckConstructorChainCycles(TypeSymbol owner, IReadOnlyList<BoundFunction> bound)
	{
		if (bound.Count == 0)
			return;

		var order = new Dictionary<FunctionSymbol, int>(bound.Count);
		for (var index = 0; index < bound.Count; index++)
			order[bound[index].Symbol] = index;

		// -1 — ребра нет (корень или несвязанная цепочка).
		var edges = new int[bound.Count];
		for (var index = 0; index < bound.Count; index++)
		{
			edges[index] = -1;
			if (bound[index].Chain is { } chain &&
				order.TryGetValue(chain.Target, out var target) &&
				target != index)
				edges[index] = target;
		}

		// Обратный обход от корней: узлы, достижимые из корня в обратном графе, — это
		// конструкторы, чья цепочка рано или поздно заканчивается корнем.
		var reachesRoot = new bool[bound.Count];
		var reverse = new List<int>[bound.Count];
		for (var index = 0; index < bound.Count; index++)
			reverse[index] = [];
		var queue = new Queue<int>();
		for (var index = 0; index < bound.Count; index++)
		{
			if (edges[index] < 0)
			{
				reachesRoot[index] = true;
				queue.Enqueue(index);
			}
			else
			{
				reverse[edges[index]].Add(index);
			}
		}

		while (queue.Count > 0)
		{
			var current = queue.Dequeue();
			foreach (var previous in reverse[current])
			{
				if (reachesRoot[previous])
					continue;
				reachesRoot[previous] = true;
				queue.Enqueue(previous);
			}
		}

		// SCC Тарьяна: цикл — компонента из двух и более узлов. Все её члены взаимно
		// достижимы, поэтому достаточно проверить достижимость корня у любого из них.
		// Узлы, достигающие корня, в обход не входят — их компоненты заведомо безопасны.
		var indices = new int[bound.Count];
		var lowLinks = new int[bound.Count];
		var onStack = new bool[bound.Count];
		var stack = new Stack<int>();
		var nextIndex = 0;

		void Visit(int node)
		{
			indices[node] = lowLinks[node] = ++nextIndex;
			stack.Push(node);
			onStack[node] = true;

			var next = edges[node];
			if (next >= 0 && !reachesRoot[next])
			{
				if (indices[next] == 0)
				{
					Visit(next);
					lowLinks[node] = System.Math.Min(lowLinks[node], lowLinks[next]);
				}
				else if (onStack[next])
				{
					lowLinks[node] = System.Math.Min(lowLinks[node], indices[next]);
				}
			}

			if (lowLinks[node] != indices[node])
				return;

			var members = new List<int>();
			int member;
			do
			{
				member = stack.Pop();
				onStack[member] = false;
				members.Add(member);
			}
			while (member != node);

			if (members.Count < 2 || reachesRoot[node])
				return;

			var last = members.Max();
			Report(
				"KRN2191",
				$"Цепочка конструкторов типа '{owner.Name}' образует цикл",
				bound[last].Chain!.Span);
		}

		for (var start = 0; start < bound.Count; start++)
			if (indices[start] == 0 && !reachesRoot[start])
				Visit(start);
	}
}
