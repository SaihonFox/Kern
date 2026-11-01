namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Наследование типов: прямые родители уже разрешены <see cref="TypeResolver"/> в
/// <see cref="TypeSymbol.Bases"/>. Однонаследственный обход (<see cref="Chain"/>) остаётся для
/// поиска ближайшего общего предка, а для раскладки и конструкторов используется
/// <see cref="Ancestors"/> — топологический порядок всех предков без повторов (slice A3a).
/// Ромбовидное наследование пока отвергается диагностикой (см. <see cref="DiamondAncestor"/>).
/// </summary>
public static class Inheritance
{
	/// <summary>
	/// Цепочка от получателя к корню включительно по первому родителю. Первый элемент — сам
	/// <paramref name="receiver"/>; повторные символы (цикл наследования) отсекаются, чтобы
	/// обход всегда завершался.
	/// </summary>
	public static IEnumerable<ClassType> Chain(ClassType receiver)
	{
		ArgumentNullException.ThrowIfNull(receiver);
		var visited = new HashSet<TypeSymbol>(ReferenceEqualityComparer.Instance);
		ClassType? current = receiver;
		while (current is not null && visited.Add(current.Symbol))
		{
			yield return current;
			current = NearestBase(current);
		}
	}

	/// <summary>
	/// Прямой родитель-класс типа: первый <see cref="ClassType"/> из разрешённых
	/// <see cref="TypeSymbol.Bases"/>. Generic-параметры остаются в терминах объявления родителя.
	/// </summary>
	public static ClassType? DirectBase(TypeSymbol symbol)
	{
		ArgumentNullException.ThrowIfNull(symbol);
		foreach (var baseType in symbol.Bases)
			if (baseType is ClassType @class)
				return @class;

		return null;
	}

	/// <summary>
	/// Все прямые родители-классы в порядке объявления: <c>type C()[A, B]</c> даёт <c>[A, B]</c>.
	/// Не-class родители (unit, ошибки) пропускаются, как и раньше.
	/// </summary>
	public static IReadOnlyList<ClassType> DirectBases(TypeSymbol symbol)
	{
		ArgumentNullException.ThrowIfNull(symbol);
		var result = new List<ClassType>(symbol.Bases.Count);
		foreach (var baseType in symbol.Bases)
			if (baseType is ClassType @class)
				result.Add(@class);

		return result;
	}

	/// <summary>
	/// Все предки типа в порядке «предок раньше потомка»: прямые родители в порядке объявления,
	/// у каждого родителя сначала его собственные предки. Повторы (ромб) отсекаются по символу:
	/// например, <c>C()[A, B]</c> при <c>A[D]</c> и <c>B[D]</c> даёт <c>[D, A, B]</c>. Сам тип в
	/// результат не входит.
	/// </summary>
	public static IReadOnlyList<TypeSymbol> Ancestors(TypeSymbol symbol)
	{
		ArgumentNullException.ThrowIfNull(symbol);
		var ordered = new List<TypeSymbol>();
		var visited = new HashSet<TypeSymbol>(ReferenceEqualityComparer.Instance) { symbol };
		foreach (var baseType in DirectBases(symbol))
			AddAncestors(baseType.Symbol, ordered, visited);

		return ordered;
	}

	/// <summary>
	/// Цепочка получателя и всех его предков-классов с подставленными аргументами: сам тип,
	/// прямые родители в порядке объявления, затем их родители (обход в ширину). Повтор
	/// (ромб) отсекается по символу, поэтому имя предка встречается один раз. Нужна для
	/// выбора родителя в выражении <c>$[A]member</c>: член ищется в типе с аргументами,
	/// а не в объявлении <c>A&lt;T&gt;</c>.
	/// </summary>
	public static IReadOnlyList<ClassType> Lineage(ClassType receiver)
	{
		ArgumentNullException.ThrowIfNull(receiver);
		var ordered = new List<ClassType>();
		var visited = new HashSet<TypeSymbol>(ReferenceEqualityComparer.Instance);
		var queue = new Queue<ClassType>();
		queue.Enqueue(receiver);
		while (queue.Count > 0)
		{
			var current = queue.Dequeue();
			if (!visited.Add(current.Symbol))
				continue;
			ordered.Add(current);
			var map = ArgumentsMap(current);
			foreach (var baseType in current.Symbol.Bases)
				if (TypeSubstitution.Apply(baseType, map) is ClassType @class)
					queue.Enqueue(@class);
		}

		return ordered;
	}

	private static void AddAncestors(TypeSymbol symbol, List<TypeSymbol> ordered, HashSet<TypeSymbol> visited)
	{
		if (!visited.Add(symbol))
			return;

		foreach (var baseType in DirectBases(symbol))
			AddAncestors(baseType.Symbol, ordered, visited);
		ordered.Add(symbol);
	}

	/// <summary>
	/// Общий предок, достижимый по двум путям (ромб), или <c>null</c>. Проверяются только ветви
	/// прямых родителей: ромб внутри самого родителя уже отвергнут в его собственном объявлении.
	/// Дубликат прямого родителя (<c>C[A, A]</c>) тоже считается ромбом.
	/// </summary>
	public static TypeSymbol? DiamondAncestor(TypeSymbol symbol)
	{
		ArgumentNullException.ThrowIfNull(symbol);
		var reachable = new HashSet<TypeSymbol>(ReferenceEqualityComparer.Instance);
		foreach (var baseType in DirectBases(symbol))
		{
			var branch = new HashSet<TypeSymbol>(ReferenceEqualityComparer.Instance);
			CollectWithSelf(baseType.Symbol, branch);
			foreach (var ancestor in branch)
				if (!reachable.Add(ancestor))
					return ancestor;
		}

		return null;
	}

	private static void CollectWithSelf(TypeSymbol symbol, HashSet<TypeSymbol> into)
	{
		if (!into.Add(symbol))
			return;

		foreach (var baseType in DirectBases(symbol))
			CollectWithSelf(baseType.Symbol, into);
	}

	/// <summary>
	/// Цепочка символов от типа к корню (первый родитель): используется статическими проверками
	/// (init-свойства, операторы, dispose), которым аргументы дженерика не нужны.
	/// </summary>
	public static IEnumerable<TypeSymbol> SymbolChain(TypeSymbol symbol)
	{
		ArgumentNullException.ThrowIfNull(symbol);
		var visited = new HashSet<TypeSymbol>(ReferenceEqualityComparer.Instance);
		TypeSymbol? current = symbol;
		while (current is not null && visited.Add(current))
		{
			yield return current;
			current = DirectBase(current)?.Symbol;
		}
	}

	/// <summary>
	/// Ближайший родитель с подставленными аргументами приёмника: <c>Derived&lt;i32&gt;</c>
	/// видит <c>Base&lt;i32&gt;</c>, а не <c>Base&lt;T&gt;</c>.
	/// </summary>
	public static ClassType? NearestBase(ClassType type)
	{
		ArgumentNullException.ThrowIfNull(type);
		if (type.Symbol.Bases.Count == 0)
			return null;

		var map = ArgumentsMap(type);
		foreach (var baseType in type.Symbol.Bases)
			if (TypeSubstitution.Apply(baseType, map) is ClassType @class)
				return @class;

		return null;
	}

	/// <summary>Карта «параметр типа → аргумент» для получателя (пустая у не-generic типа).</summary>
	public static Dictionary<string, KernType> ArgumentsMap(ClassType type)
	{
		ArgumentNullException.ThrowIfNull(type);
		var map = new Dictionary<string, KernType>(StringComparer.Ordinal);
		for (var index = 0; index < type.Symbol.GenericParameterNames.Count && index < type.Arguments.Count; index++)
			map[type.Symbol.GenericParameterNames[index]] = type.Arguments[index];
		return map;
	}
}
