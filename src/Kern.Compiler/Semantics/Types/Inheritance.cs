namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Однонаследственная цепочка типов (slice A1). Родители уже разрешены
/// <see cref="TypeResolver"/> в <see cref="TypeSymbol.Bases"/>; здесь они подставляются
/// аргументами приёмника и обходятся от наследника к корню. Множественное наследование
/// (<c>[A, B]</c>) пока не поддержано: берётся первый родитель-класс, остальные остаются
/// за следующей итерацией (см. <c>DECISIONS_NEEDED.md</c>).
/// </summary>
public static class Inheritance
{
	/// <summary>
	/// Цепочка от получателя к корню включительно. Первый элемент — сам <paramref name="receiver"/>;
	/// повторные символы (цикл наследования) отсекаются, чтобы обход всегда завершался.
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
	/// <see cref="TypeSymbol.Bases"/>. Generic-параметры пока остаются в терминах
	/// объявления родителя (<see cref="TypeParameterType"/>).
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
	/// Цепочка символов от типа к корню: используется статическими проверками (init-свойства,
	/// операторы, dispose), которым аргументы дженерика не нужны.
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
