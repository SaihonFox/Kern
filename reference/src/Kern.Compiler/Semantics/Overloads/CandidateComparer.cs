using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Semantics.Overloads;

/// <summary>
/// Выбор лучшего из применимых кандидатов («better function member» из C#):
/// сначала сравнение преобразований каждого аргумента, затем тай-брейки при эквивалентных преобразованиях.
/// </summary>
internal static class CandidateComparer
{
	/// <summary>1 — <paramref name="a"/> лучше; -1 — лучше <paramref name="b"/>; 0 — ни один не лучше.</summary>
	public static int Compare(ResolvedCall a, ResolvedCall b, OverloadRequest req)
	{
		int better = 0, worse = 0;

		if (a.ReceiverTarget is not null && b.ReceiverTarget is not null)
			Tally(CompareTargets(req.Receiver!, a.ReceiverTarget, b.ReceiverTarget), ref better, ref worse);
		for (int i = 0; i < req.Arguments.Count; i++)
			Tally(CompareTargets(req.Arguments[i].Type, a.ArgumentTargets[i], b.ArgumentTargets[i]), ref better, ref worse);

		if (better > 0 && worse == 0) return 1;
		if (worse > 0 && better == 0) return -1;
		if (better > 0 && worse > 0) return 0;

		return TieBreak(a, b);
	}

	private static void Tally(int c, ref int better, ref int worse)
	{
		if (c > 0) better++;
		else if (c < 0) worse++;
	}

	/// <summary>Какое из преобразований аргумента типа <paramref name="argType"/> лучше: к <paramref name="t1"/> или к <paramref name="t2"/>.</summary>
	public static int CompareTargets(TypeSymbol argType, TypeSymbol t1, TypeSymbol t2)
	{
		if (t1.Equals(t2)) return 0;
		if (argType.Equals(t1)) return 1;
		if (argType.Equals(t2)) return -1;

		bool i12 = Conversions.IsImplicit(t1, t2);
		bool i21 = Conversions.IsImplicit(t2, t1);
		if (i12 && !i21) return 1;
		if (i21 && !i12) return -1;

		if (t1 is PrimitiveType p1 && t2 is PrimitiveType p2)
		{
			if (p1.IsSignedInt && p2.IsUnsignedInt) return 1;
			if (p2.IsSignedInt && p1.IsUnsignedInt) return -1;
		}

		// Функции с одинаковыми параметрами: лучше тот, чей результат лучше (непустой результат лучше void).
		if (t1 is FuncType f1 && t2 is FuncType f2 && f1.Params.Count == f2.Params.Count && f1.Params.Zip(f2.Params).All(p => p.First.Equals(p.Second)))
		{
			bool v1 = f1.Return == PrimitiveType.Void, v2 = f2.Return == PrimitiveType.Void;
			if (v1 != v2) return v1 ? -1 : 1;
			var argReturn = argType is FuncType af ? af.Return : f1.Return;
			return CompareTargets(argReturn, f1.Return, f2.Return);
		}
		return 0;
	}

	private static int TieBreak(ResolvedCall a, ResolvedCall b)
	{
		// 1. не-generic лучше generic
		if (a.IsGenericInstantiation != b.IsGenericInstantiation) return a.IsGenericInstantiation ? -1 : 1;
		// 2. нормальная форма лучше расширенной
		if (a.IsExpandedForm != b.IsExpandedForm) return a.IsExpandedForm ? -1 : 1;
		// 3. меньше значений по умолчанию лучше
		if (a.DefaultsUsed != b.DefaultsUsed) return a.DefaultsUsed < b.DefaultsUsed ? 1 : -1;
		// 4. обе расширенные: больше объявленных параметров — специфичнее
		if (a.IsExpandedForm && b.IsExpandedForm && a.Function.Parameters.Count != b.Function.Parameters.Count)
			return a.Function.Parameters.Count > b.Function.Parameters.Count ? 1 : -1;
		// 5. более специфичные объявленные типы параметров
		int specific = Specificity(a, b);
		if (specific != 0) return specific;
		// 6. обычный член лучше расширения
		if (a.Function.IsExtension != b.Function.IsExtension) return a.Function.IsExtension ? -1 : 1;
		return 0;
	}

	private static int Specificity(ResolvedCall a, ResolvedCall b)
	{
		int better = 0, worse = 0;
		if (a.DeclaredReceiverTarget is not null && b.DeclaredReceiverTarget is not null)
			Tally(MoreSpecific(a.DeclaredReceiverTarget, b.DeclaredReceiverTarget), ref better, ref worse);
		for (int i = 0; i < a.DeclaredTargets.Count; i++)
			Tally(MoreSpecific(a.DeclaredTargets[i], b.DeclaredTargets[i]), ref better, ref worse);
		return better > 0 && worse == 0 ? 1 : worse > 0 && better == 0 ? -1 : 0;
	}

	/// <summary>1 — <paramref name="x"/> специфичнее (`list&lt;T&gt;` специфичнее `T`, конкретный тип специфичнее параметра типа).</summary>
	private static int MoreSpecific(TypeSymbol x, TypeSymbol y)
	{
		if (x is TypeParamType && y is not TypeParamType) return -1;
		if (y is TypeParamType && x is not TypeParamType) return 1;
		if (!TypeShape.SameConstructor(x, y)) return 0;

		var xc = TypeShape.Children(x);
		var yc = TypeShape.Children(y);
		int better = 0, worse = 0;
		for (int i = 0; i < xc.Count; i++) Tally(MoreSpecific(xc[i], yc[i]), ref better, ref worse);
		return better > 0 && worse == 0 ? 1 : worse > 0 && better == 0 ? -1 : 0;
	}

	/// <summary>
	/// Убирает кандидатов, перекрытых переопределением в более производном типе
	/// (одинаковая сигнатура, объявление одного — потомок объявления другого).
	/// </summary>
	public static List<ResolvedCall> PruneOverridden(List<ResolvedCall> calls)
	{
		if (calls.Count < 2) return calls;
		return [.. calls.Where(c => !calls.Any(o =>
			!ReferenceEquals(o, c) && o.Function.HasSameSignature(c.Function) && IsStrictlyDerived(o.Function.DeclaringType, c.Function.DeclaringType)))];
	}

	private static bool IsStrictlyDerived(TypeDeclSymbol? derived, TypeDeclSymbol? baseDecl)
	{
		if (derived is null || baseDecl is null || ReferenceEquals(derived, baseDecl)) return false;
		var seen = new HashSet<TypeDeclSymbol>();
		var stack = new Stack<TypeDeclSymbol>();
		stack.Push(derived);
		while (stack.Count > 0)
		{
			var d = stack.Pop();
			foreach (var p in d.Parents)
				if (p.Decl is { } pd && seen.Add(pd))
				{
					if (ReferenceEquals(pd, baseDecl)) return true;
					stack.Push(pd);
				}
		}
		return false;
	}
}
