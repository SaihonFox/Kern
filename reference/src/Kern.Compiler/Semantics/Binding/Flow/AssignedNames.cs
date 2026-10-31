using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding.Flow;

/// <summary>
/// Синтаксический предпросмотр: имена переменных, которым где-либо в поддереве присваивается значение (`x = ..`, `x += ..`).
/// Консервативно (по именам): используется для сброса smart-cast в циклах/лямбдах и для признака «изменяемая захваченная переменная».
/// </summary>
public static class AssignedNames
{
	public static HashSet<string> In(Node root)
	{
		var set = new HashSet<string>();
		AstWalker.Walk(root, n =>
		{
			if (n is AssignExpr { Target: IdentExpr id }) set.Add(id.Name);
			if (n is LoopExpr le) AddLoopTargets(le.Variable, set);
		});
		return set;
	}

	/// <summary>`loop i in …` с уже объявленной `i` присваивает ей на каждой итерации.</summary>
	private static void AddLoopTargets(Pattern p, HashSet<string> set)
	{
		switch (p)
		{
			case BindingPattern { Declares: false } b: set.Add(b.Name); break;
			case DestructurePattern d: foreach (var e in d.Elements) AddLoopTargets(e, set); break;
			case NamedPattern n: AddLoopTargets(n.Inner, set); break;
		}
	}
}
