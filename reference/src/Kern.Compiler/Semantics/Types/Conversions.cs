using Kern.Bytecode;
using Kern.Compiler.Semantics.Symbols;

namespace Kern.Compiler.Semantics.Types;

public enum ConversionKind
{
	None,
	Identity,
	/// <summary>i32 → i64, f32 → f64, u8 → i16, inum ← i32 …</summary>
	ImplicitNumeric,
	/// <summary>T → T? (и T? → U?, если T → U).</summary>
	ToNullable,
	/// <summary>nil → T?</summary>
	NilToNullable,
	/// <summary>`none` совместим с любым типом.</summary>
	FromNone,
	/// <summary>Потомок → предок (в том числе → bound generic-параметра).</summary>
	Subtype,
	/// <summary>Любое значение → object.</summary>
	ToObject,
	/// <summary>Член (в т.ч. unit) → union.</summary>
	ToUnion,
	/// <summary>Структурная конверсия кортежа/функции.</summary>
	Structural,
}

/// <summary>Правила неявных конверсий. Явные приведения (`as`, конструкторы) — в части 2.</summary>
public static class Conversions
{
	public static ConversionKind Classify(TypeSymbol from, TypeSymbol to)
	{
		if (from.IsError || to.IsError) return ConversionKind.Identity;
		if (from.Equals(to)) return ConversionKind.Identity;
		if (from is NeverType) return ConversionKind.Subtype;
		if (from is NoneType) return ConversionKind.FromNone;

		if (from is NilType) return to is NullableType ? ConversionKind.NilToNullable : ConversionKind.None;

		if (to is NullableType tn)
		{
			if (from is NullableType fn)
				return Classify(fn.Inner, tn.Inner) != ConversionKind.None ? ConversionKind.ToNullable : ConversionKind.None;
			return Classify(from, tn.Inner) != ConversionKind.None ? ConversionKind.ToNullable : ConversionKind.None;
		}
		// `T?` → `object`: значение (или nil) передаётся как есть — нужно `println(x)` для nullable
		if (from is NullableType && to is PrimitiveType { Kind: PrimKind.Object }) return ConversionKind.ToObject;
		if (from is NullableType) return ConversionKind.None;

		if (to is PrimitiveType tp)
		{
			if (tp.Kind == PrimKind.Object) return from is PrimitiveType { Kind: PrimKind.Void } ? ConversionKind.None : ConversionKind.ToObject;
			if (from is PrimitiveType fp && IsNumericConversion(fp, tp)) return ConversionKind.ImplicitNumeric;
		}

		if (to is UnionType tu)
		{
			if (from is UnionType fu)
			{
				var targets = tu.Members;
				if (fu.Members.All(m => targets.Any(t => Classify(m, t) != ConversionKind.None))) return ConversionKind.ToUnion;
				return IsSubtype(from, to) ? ConversionKind.Subtype : ConversionKind.None;
			}
			if (tu.Members.Any(m => Classify(from, m) is not ConversionKind.None)) return ConversionKind.ToUnion;
		}

		// `Ok(5)` без известного E имеет тип result<i32, never>: never-половина приводится к любой.
		if (from is ResultType fr && to is ResultType tr
			&& (fr.Ok is NeverType || fr.Ok.Equals(tr.Ok)) && (fr.Err is NeverType || fr.Err.Equals(tr.Err)))
			return ConversionKind.Structural;

		if (IsSubtype(from, to)) return ConversionKind.Subtype;

		if (from is TypeParamType tpt && tpt.Param.Bounds.Any(b => Classify(b, to) is not ConversionKind.None))
			return ConversionKind.Subtype;

		if (from is FuncType ff && to is FuncType tf && ff.Params.Count == tf.Params.Count
			&& ff.Params.Zip(tf.Params).All(p => p.First.Equals(p.Second))
			&& (ff.Return.Equals(tf.Return) || IsSubtype(ff.Return, tf.Return)))
			return ConversionKind.Structural;

		if (from is TupleType ft && to is TupleType tt && ft.Items.Count == tt.Items.Count
			&& ft.Items.Zip(tt.Items).All(p => Classify(p.First, p.Second) is not ConversionKind.None))
			return ConversionKind.Structural;

		return ConversionKind.None;
	}

	public static bool IsImplicit(TypeSymbol from, TypeSymbol to) => Classify(from, to) != ConversionKind.None;

	/// <summary>Числовое повышение в семействе и приведение к универсальным inum/fnum/num.</summary>
	public static bool IsNumericConversion(PrimitiveType from, PrimitiveType to)
	{
		if (from.Equals(to)) return true;
		switch (to.Kind)
		{
			case PrimKind.INum: return from.IsInteger || from.Kind == PrimKind.INum;
			case PrimKind.FNum: return from.IsFloat || from.Kind == PrimKind.FNum;
			case PrimKind.Num: return from.IsNumeric || from.IsUniversalNumeric;
		}
		if (!from.IsNumeric || !to.IsNumeric) return false;
		if (from.IsFloat) return to.IsFloat && to.Bits > from.Bits;
		// целые: знаковость сохраняется, беззнаковое → более широкое знаковое
		if (from.IsSignedInt) return to.IsSignedInt && to.Bits > from.Bits;
		return to.IsInteger && to.Bits > from.Bits;
	}

	/// <summary>Все базовые типы (транзитивно) с подставленными generic-аргументами.</summary>
	public static IEnumerable<TypeSymbol> BaseTypes(TypeSymbol type)
	{
		var seen = new HashSet<TypeSymbol>();
		var stack = new Stack<TypeSymbol>();
		stack.Push(type);
		while (stack.Count > 0)
		{
			var t = stack.Pop();
			if (NativeType.CollectionParentOf(t) is { } cp)
			{
				if (seen.Add(cp)) yield return cp;
				continue;
			}
			if (t is NativeType nat)
			{
				if (nat.BaseType is { } nb && seen.Add(nb))
				{
					yield return nb;
					stack.Push(nb);
				}
				continue;
			}
			if (t is not DeclaredType d) continue;
			var map = d.Args.Count == 0 ? null : d.TypeMap();
			foreach (var p in d.Decl.Parents)
			{
				var pt = map is null ? p.Type : p.Type.Substitute(map);
				if (seen.Add(pt))
				{
					yield return pt;
					stack.Push(pt);
				}
			}
		}
	}

	/// <summary>Является ли <paramref name="from"/> строгим потомком <paramref name="to"/> (по спискам родителей).</summary>
	public static bool IsSubtype(TypeSymbol from, TypeSymbol to)
	{
		if (from.Equals(to)) return true;
		if (from is not DeclaredType) return false;
		foreach (var b in BaseTypes(from))
			if (b.Equals(to)) return true;
		return false;
	}

	/// <summary>Общий тип двух выражений для вывода коллекций/ветвей: повышение числа, T/nil → T?; null — не найден.</summary>
	public static TypeSymbol? CommonType(TypeSymbol a, TypeSymbol b)
	{
		if (a.IsError) return b;
		if (b.IsError) return a;
		if (a is NeverType) return b;
		if (b is NeverType) return a;
		if (a is NoneType) return b;
		if (b is NoneType) return a;
		if (a is NilType) return b is NullableType ? b : NullableType.Of(b);
		if (b is NilType) return a is NullableType ? a : NullableType.Of(a);
		if (IsImplicit(a, b)) return b;
		if (IsImplicit(b, a)) return a;
		return null;
	}

	/// <summary>Проверка ограничений generic-параметра для аргумента; <paramref name="reason"/> — причина отказа.</summary>
	public static bool SatisfiesConstraints(
		TypeParamSymbol param, TypeSymbol arg, IReadOnlyDictionary<TypeParamSymbol, TypeSymbol> map, out string? reason)
	{
		reason = null;
		if (arg.IsError) return true;
		foreach (var bound in param.Bounds)
		{
			var b = bound.Substitute(map);
			if (b.IsError) continue;
			if (!IsImplicit(arg, b))
			{
				reason = $"тип '{arg.ToDisplayString()}' не удовлетворяет ограничению '{b.ToDisplayString()}' параметра '{param.Name}'";
				return false;
			}
		}
		foreach (var ctor in param.CtorConstraints)
		{
			if (arg is TypeParamType) continue;
			if (arg is not DeclaredType d) continue; // нативные/встроенные: проверка в части 2
			var types = ctor.Select(t => t.Substitute(map)).ToList();
			var ctors = d.Decl.Constructors;
			bool ok = ctors.Any(c => c.Parameters.Count >= types.Count
				&& types.Select((t, i) => IsImplicit(t, c.Parameters[i].Type.Substitute(d.TypeMap()))).All(x => x)
				&& c.Parameters.Skip(types.Count).All(p => p.HasDefault || p.IsVariadic))
				|| (types.Count == 0 && ctors.Count == 0);
			if (!ok)
			{
				reason = $"у типа '{arg.ToDisplayString()}' нет конструктора ({string.Join(", ", types.Select(t => t.ToDisplayString()))}), требуемого параметром '{param.Name}'";
				return false;
			}
		}
		return true;
	}
}
