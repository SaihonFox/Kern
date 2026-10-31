using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Overloads;
using Kern.Compiler.Semantics.Types;
using Kern.Core;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding.Core;

public sealed partial class Binder
{
	/// <summary>
	/// Общий тип элементов: числовые литералы без суффикса подстраиваются под остальные элементы,
	/// иначе — <see cref="Conversions.CommonType"/>. null — вывести нельзя (ошибка уже сообщена).
	/// </summary>
	private TypeSymbol? UnifyElements(IReadOnlyList<BoundExpr> elements, TextSpan span, string what)
	{
		TypeSymbol? acc = null;
		foreach (var e in elements)
		{
			if (e.IsError) continue;
			var t = e.Type;
			if (t is NeverType) continue;
			if (acc is null) { acc = t; continue; }
			if (e is BoundNumberLiteral { Value.Suffix: null } lit && ConstantConversions.CanConvert(lit.Value, acc)) continue;
			if (acc is PrimitiveType && elements.Any(x => x is BoundNumberLiteral { Value.Suffix: null } xl && xl.Type.Equals(acc))
				&& e.Type is PrimitiveType { IsNumeric: true } && ConstantConversions.CanConvert(((BoundNumberLiteral)elements.First(x => x is BoundNumberLiteral { Value.Suffix: null } xl2 && xl2.Type.Equals(acc))).Value, t))
			{
				acc = t;
				continue;
			}
			var common = Conversions.CommonType(acc, t);
			if (common is null)
			{
				Error(Krn.CollectionTypeInference, $"элементы {what} имеют несовместимые типы '{acc.ToDisplayString()}' и '{t.ToDisplayString()}'", e.Span);
				return null;
			}
			acc = common;
		}
		if (acc is null) return ErrorType.Instance;
		return acc is NilType or NoneType ? null : acc;
	}

	private BoundExpr BindListLiteral(Syn.ListExpr li, TypeSymbol? expected)
	{
		TypeSymbol? elem = li.ElementType is null ? (expected as ListType)?.Element : ResolveType(li.ElementType);
		var elements = li.Elements.Select(e => Bind(e, elem)).ToList();
		if (elem is null)
		{
			if (elements.Count == 0)
				return ErrorExpr(Krn.CollectionTypeInference, "пустой список: укажите тип `<T>[]` или ожидаемый тип", li.Span);
			elem = UnifyElements(elements, li.Span, "списка");
			if (elem is null) return new BoundErrorExpr(li.Span);
		}
		var converted = elements.Select((e, i) => Convert(e, elem, li.Elements[i].Span)).ToList();
		return new BoundList(converted, new ListType(elem), li.Span);
	}

	private BoundExpr BindArrayLiteral(Syn.ArrayExpr ar, TypeSymbol? expected)
	{
		TypeSymbol? elem = ar.ElementType is null ? (expected as ArrayType)?.Element : ResolveType(ar.ElementType);
		var elements = ar.Elements.Select(e => Bind(e, elem)).ToList();
		if (elem is null)
		{
			if (elements.Count == 0)
				return ErrorExpr(Krn.CollectionTypeInference, "пустой массив: укажите тип `array<T>[]`", ar.Span);
			elem = UnifyElements(elements, ar.Span, "массива");
			if (elem is null) return new BoundErrorExpr(ar.Span);
		}
		var converted = elements.Select((e, i) => Convert(e, elem, ar.Elements[i].Span)).ToList();
		return new BoundArray(converted, new ArrayType(elem), ar.Span);
	}

	private BoundExpr BindDictLiteral(Syn.DictExpr d, TypeSymbol? expected)
	{
		var ed = expected as DictType;
		TypeSymbol? key = d.KeyType is null ? ed?.Key : ResolveType(d.KeyType);
		TypeSymbol? value = d.ValueType is null ? ed?.Value : ResolveType(d.ValueType);
		var keys = d.Entries.Select(e => Bind(e.Key, key)).ToList();
		var values = d.Entries.Select(e => Bind(e.Value, value)).ToList();
		if (key is null || value is null)
		{
			if (d.Entries.Count == 0)
				return ErrorExpr(Krn.CollectionTypeInference, "пустой словарь: укажите типы `<K: V>[:]`", d.Span);
			key ??= UnifyElements(keys, d.Span, "ключей словаря");
			value ??= UnifyElements(values, d.Span, "значений словаря");
			if (key is null || value is null) return new BoundErrorExpr(d.Span);
		}
		var entries = new List<BoundDictEntry>();
		for (int i = 0; i < d.Entries.Count; i++)
			entries.Add(new BoundDictEntry(Convert(keys[i], key, d.Entries[i].Key.Span), Convert(values[i], value, d.Entries[i].Value.Span), d.Entries[i].Span));
		return new BoundDict(entries, new DictType(key, value), d.Span);
	}

	private BoundExpr BindTuple(Syn.TupleExpr t, TypeSymbol? expected)
	{
		var et = expected as TupleType;
		var items = new List<BoundExpr>();
		var types = new List<TypeSymbol>();
		var names = new List<string?>();
		for (int i = 0; i < t.Elements.Count; i++)
		{
			var arg = t.Elements[i];
			var hint = et is not null && i < et.Items.Count ? et.Items[i] : null;
			var v = Bind(arg.Value, hint);
			items.Add(v);
			types.Add(v.IsError ? ErrorType.Instance : v.Type is NilType or NoneType ? v.Type : v.Type);
			names.Add(arg.Name);
		}
		if (et is not null && et.Items.Count == items.Count)
		{
			for (int i = 0; i < items.Count; i++)
				if (Conversions.IsImplicit(types[i], et.Items[i]))
				{
					items[i] = Convert(items[i], et.Items[i], t.Elements[i].Span);
					types[i] = et.Items[i];
				}
		}
		for (int i = 0; i < items.Count; i++)
			if (types[i] is NilType or NoneType) { InferStorageType(items[i], t.Elements[i].Span); types[i] = ErrorType.Instance; }
		return new BoundTuple(items, new TupleType(types, names), t.Span);
	}
}
