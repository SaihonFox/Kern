using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace Kern.Compiler.Semantics.Bound;

/// <summary>
/// Универсальный обход типизированного дерева (по аналогии с <c>AstWalker</c>): дочерние узлы определяются по
/// публичным свойствам записи — <see cref="BoundNode"/> и коллекциям узлов. Новые узлы поддерживаются автоматически.
/// Символы и типы (не узлы) пропускаются.
/// </summary>
public static class BoundWalker
{
	private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Cache = new();

	private static PropertyInfo[] GetProps(Type type) => Cache.GetOrAdd(type, static t =>
		t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is not null
						&& p.Name != nameof(BoundNode.Type)
						&& (typeof(BoundNode).IsAssignableFrom(p.PropertyType)
							|| (p.PropertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(p.PropertyType))))
			.OrderBy(p => p.MetadataToken)
			.ToArray());

	public static IEnumerable<BoundNode> Children(BoundNode node)
	{
		foreach (var prop in GetProps(node.GetType()))
		{
			var value = prop.GetValue(node);
			if (value is BoundNode child) yield return child;
			else if (value is IEnumerable items and not string)
				foreach (var item in items)
					if (item is BoundNode n) yield return n;
		}
	}

	/// <summary>Узел и все потомки в прямом порядке (pre-order).</summary>
	public static IEnumerable<BoundNode> DescendantsAndSelf(BoundNode root)
	{
		var stack = new Stack<BoundNode>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			var node = stack.Pop();
			yield return node;
			foreach (var child in Children(node).Reverse()) stack.Push(child);
		}
	}

	public static IEnumerable<BoundNode> Descendants(BoundNode root) => DescendantsAndSelf(root).Skip(1);

	/// <summary>Обход с отсечением: если <paramref name="visit"/> вернул false, потомки узла пропускаются.</summary>
	public static void Walk(BoundNode root, Func<BoundNode, bool> visit)
	{
		var stack = new Stack<BoundNode>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			var node = stack.Pop();
			if (!visit(node)) continue;
			foreach (var child in Children(node).Reverse()) stack.Push(child);
		}
	}
}
