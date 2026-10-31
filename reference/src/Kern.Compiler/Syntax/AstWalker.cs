using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace Kern.Compiler.Syntax;

/// <summary>
/// Универсальный обход AST. Дочерние узлы определяются по публичным свойствам записи:
/// Node и коллекции Node (в порядке объявления параметров). Новые узлы поддерживаются автоматически.
/// </summary>
public static class AstWalker
{
	private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Cache = new();

	private static PropertyInfo[] GetProps(Type type) => Cache.GetOrAdd(type, static t =>
		t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is not null
						&& (typeof(Node).IsAssignableFrom(p.PropertyType)
							|| (p.PropertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(p.PropertyType))))
			.OrderBy(p => p.MetadataToken)
			.ToArray());

	/// <summary>Прямые дочерние узлы.</summary>
	public static IEnumerable<Node> Children(Node node)
	{
		foreach (var prop in GetProps(node.GetType()))
		{
			var value = prop.GetValue(node);
			if (value is Node child) yield return child;
			else if (value is IEnumerable items and not string)
				foreach (var item in items)
					if (item is Node n) yield return n;
		}
	}

	/// <summary>Узел и все потомки, в прямом порядке (pre-order).</summary>
	public static IEnumerable<Node> DescendantsAndSelf(Node root)
	{
		var stack = new Stack<Node>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			var node = stack.Pop();
			yield return node;
			foreach (var child in Children(node).Reverse()) stack.Push(child);
		}
	}

	public static IEnumerable<Node> Descendants(Node root) => DescendantsAndSelf(root).Skip(1);

	/// <summary>Вызывает action для каждого узла поддерева (pre-order).</summary>
	public static void Walk(Node root, Action<Node> action)
	{
		foreach (var node in DescendantsAndSelf(root)) action(node);
	}

	/// <summary>Обход с отсечением: если visit вернул false, потомки узла пропускаются.</summary>
	public static void Walk(Node root, Func<Node, bool> visit)
	{
		var stack = new Stack<Node>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			var node = stack.Pop();
			if (!visit(node)) continue;
			foreach (var child in Children(node).Reverse()) stack.Push(child);
		}
	}
}
