using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Semantics.Overloads;

/// <summary>Обход структуры типа: свободные generic-параметры и дочерние типы.</summary>
internal static class TypeShape
{
	/// <summary>Дочерние типы-аргументы составного типа (пусто для простых).</summary>
	public static IReadOnlyList<TypeSymbol> Children(TypeSymbol t) => t switch
	{
		NullableType n => [n.Inner],
		FuncType f => [.. f.Params, f.Return],
		TupleType tt => tt.Items,
		BuiltinGenericType b => b.Args,
		NativeType nt => nt.Args,
		DeclaredType d => d.Args,
		_ => [],
	};

	public static void CollectTypeParams(TypeSymbol t, ISet<TypeParamSymbol> into)
	{
		if (t is TypeParamType p) { into.Add(p.Param); return; }
		foreach (var c in Children(t)) CollectTypeParams(c, into);
	}

	/// <summary>Один и тот же «конструктор» типа с одинаковым числом аргументов (list&lt;_&gt; и list&lt;_&gt;, Box&lt;_&gt; и Box&lt;_&gt;).</summary>
	public static bool SameConstructor(TypeSymbol a, TypeSymbol b) => (a, b) switch
	{
		(NullableType, NullableType) => true,
		(FuncType x, FuncType y) => x.Params.Count == y.Params.Count,
		(TupleType x, TupleType y) => x.Items.Count == y.Items.Count,
		(BuiltinGenericType x, BuiltinGenericType y) => x.GetType() == y.GetType() && x.Args.Count == y.Args.Count,
		(NativeType x, NativeType y) => x.Symbol.FullName == y.Symbol.FullName && x.Args.Count == y.Args.Count,
		(DeclaredType x, DeclaredType y) => ReferenceEquals(x.Decl, y.Decl) && x.Args.Count == y.Args.Count,
		_ => false,
	};
}

/// <summary>Границы одного выводимого параметра: точные (из инвариантных позиций) и нижние (значение «втекает» в параметр).</summary>
internal sealed class InferenceBounds
{
	public List<TypeSymbol> Exact { get; } = [];
	public List<TypeSymbol> Lower { get; } = [];
}

/// <summary>
/// Вывод generic-аргументов по аргументам вызова (в духе C#: нижние и точные границы, затем фиксация).
/// Лямбды к этому моменту уже типизированы, поэтому многофазный вывод не нужен.
/// </summary>
internal sealed class TypeInferrer(IEnumerable<TypeParamSymbol> inferable)
{
	private readonly HashSet<TypeParamSymbol> _inferable = [.. inferable];
	private readonly Dictionary<TypeParamSymbol, InferenceBounds> _bounds = [];

	public bool HasInferable => _inferable.Count > 0;
	public IReadOnlyCollection<TypeParamSymbol> Inferable => _inferable;

	/// <summary>Значение типа <paramref name="argument"/> передаётся в параметр типа <paramref name="parameter"/>.</summary>
	public void InferFrom(TypeSymbol parameter, TypeSymbol argument) => Infer(parameter, argument, exact: false);

	private void Infer(TypeSymbol p, TypeSymbol a, bool exact)
	{
		if (a.IsError || a is NoneType) return;
		if (!p.ContainsTypeParam) return;

		if (p is TypeParamType tp)
		{
			if (!_inferable.Contains(tp.Param) || a is NilType) return;
			var b = Bounds(tp.Param);
			(exact ? b.Exact : b.Lower).Add(a);
			return;
		}

		switch (p)
		{
			case NullableType pn:
				Infer(pn.Inner, a is NullableType an ? an.Inner : a, exact);
				return;
			case FuncType pf when a is FuncType af && af.Params.Count == pf.Params.Count:
				for (int i = 0; i < pf.Params.Count; i++) Infer(pf.Params[i], af.Params[i], exact: true);
				Infer(pf.Return, af.Return, exact);
				return;
			case TupleType pt when a is TupleType at && at.Items.Count == pt.Items.Count:
				for (int i = 0; i < pt.Items.Count; i++) Infer(pt.Items[i], at.Items[i], exact);
				return;
			case DeclaredType pd:
			{
				var match = a is DeclaredType ad && ReferenceEquals(ad.Decl, pd.Decl)
					? ad
					: Conversions.BaseTypes(a).OfType<DeclaredType>().FirstOrDefault(b => ReferenceEquals(b.Decl, pd.Decl));
				if (match is null || match.Args.Count != pd.Args.Count) return;
				for (int i = 0; i < pd.Args.Count; i++) Infer(pd.Args[i], match.Args[i], exact: true);
				return;
			}
			default:
				if (TypeShape.SameConstructor(p, a))
				{
					var pc = TypeShape.Children(p);
					var ac = TypeShape.Children(a);
					for (int i = 0; i < pc.Count; i++) Infer(pc[i], ac[i], exact: true);
				}
				return;
		}
	}

	private InferenceBounds Bounds(TypeParamSymbol p)
	{
		if (!_bounds.TryGetValue(p, out var b)) _bounds[p] = b = new InferenceBounds();
		return b;
	}

	/// <summary>
	/// Фиксирует все выводимые параметры. false — не удалось (<paramref name="error"/> — сообщение:
	/// нет информации либо конфликт границ).
	/// </summary>
	public bool TryFix(IDictionary<TypeParamSymbol, TypeSymbol> map, out string? error)
	{
		error = null;
		foreach (var tp in _inferable)
		{
			if (map.ContainsKey(tp)) continue;
			if (!_bounds.TryGetValue(tp, out var b) || (b.Exact.Count == 0 && b.Lower.Count == 0))
			{
				error = $"не удалось вывести generic-аргумент '{tp.Name}': укажите его явно `<...>`";
				return false;
			}

			TypeSymbol? result = null;
			if (b.Exact.Count > 0)
			{
				result = b.Exact[0];
				foreach (var e in b.Exact.Skip(1))
					if (!e.Equals(result))
					{
						error = $"для '{tp.Name}' выведены конфликтующие типы '{result.ToDisplayString()}' и '{e.ToDisplayString()}'";
						return false;
					}
				foreach (var l in b.Lower)
					if (!Conversions.IsImplicit(l, result))
					{
						error = $"для '{tp.Name}' выведен тип '{result.ToDisplayString()}', но '{l.ToDisplayString()}' к нему не приводится";
						return false;
					}
			}
			else
			{
				result = b.Lower[0];
				foreach (var l in b.Lower.Skip(1))
				{
					var common = Conversions.CommonType(result, l);
					if (common is null)
					{
						error = $"для '{tp.Name}' выведены несовместимые типы '{result.ToDisplayString()}' и '{l.ToDisplayString()}'";
						return false;
					}
					result = common;
				}
			}
			map[tp] = result;
		}
		return true;
	}
}
