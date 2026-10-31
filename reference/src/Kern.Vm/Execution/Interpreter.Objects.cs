using Kern.Bytecode;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>Коллекции, итерация, деструктуризация, enum, динамический доступ по имени, копирование unit.</summary>
public sealed partial class Interpreter
{
	// ------------------------------------------------------------ итерация

	internal KIterator MakeIterator(KValue v)
	{
		if (v.Kind == ValueKind.Str) return new StrIterator(v.AsStr);
		switch (v.Ref)
		{
			case KIterator it: return it;
			case KListObject l: return new ListIterator(l.Items);
			case KArrayObject a: return new ArrayIterator(a.Items);
			case KDictObject d: return new DictIterator(d);
			case KRange r: return new RangeIterator(r);
			case KTupleObject t: return new TupleIterator(t.Items);
			case KNativeBox { Value: System.Collections.IEnumerable e }: return new ClrIterator(e.GetEnumerator());
			case KObject o:
			{
				var info = Info.Classes[o.Type.DefIndex];
				if (info.MethodsByName.TryGetValue("iter", out var iters))
				{
					foreach (int fi in iters)
						if (_funcs[fi].Params.Count == 0) return MakeIterator(CallMethod(o, fi, []));
				}
				if (info.MethodsByName.TryGetValue("has_next", out var hn) && info.MethodsByName.TryGetValue("next", out var nx))
					return new UserIterator(this, o, hn[0], nx[0]);
				break;
			}
		}
		if (v.IsNilOrNone) throw new KernPanic("итерация по nil");
		throw new KernPanic($"по значению типа {TypeOf(v).Name} нельзя итерироваться");
	}

	// ------------------------------------------------------------ индексация

	private static int IntIndex(KValue i, string what)
	{
		if (!i.IsInteger) throw new KernPanic($"индекс {what} должен быть целым, получено {i.Kind}");
		return (int)i.Bits;
	}

	private static KernPanic OutOfRange(int i, int n) => new($"индекс {i} вне диапазона (длина {n})");

	internal KValue IndexGet(KValue o, KValue i)
	{
		if (o.Kind == ValueKind.Str)
		{
			string s = o.AsStr;
			if (i.Ref is KRange sr) { var (st, ln) = SliceBounds(sr, s.Length); return KValue.Str(s.Substring(st, ln)); }
			int ix = IntIndex(i, "строки");
			return (uint)ix < (uint)s.Length ? KValue.Char(s[ix]) : throw OutOfRange(ix, s.Length);
		}
		switch (o.Ref)
		{
			case KListObject l:
				if (i.Ref is KRange lr) { var (st, ln) = SliceBounds(lr, l.Items.Count); return KValue.Obj(new KListObject(l.ElementType, l.Items.GetRange(st, ln))); }
				{ int ix = IntIndex(i, "списка"); return (uint)ix < (uint)l.Items.Count ? l.Items[ix] : throw OutOfRange(ix, l.Items.Count); }
			case KArrayObject a:
				if (i.Ref is KRange ar) { var (st, ln) = SliceBounds(ar, a.Items.Length); return KValue.Obj(new KArrayObject(a.ElementType, a.Items.AsSpan(st, ln).ToArray())); }
				{ int ix = IntIndex(i, "массива"); return (uint)ix < (uint)a.Items.Length ? a.Items[ix] : throw OutOfRange(ix, a.Items.Length); }
			case KDictObject d:
				return d.Items.TryGetValue(i, out var dv) ? dv : throw new KernPanic($"ключ не найден: {ValueOps.Repr(i)}");
			case KTupleObject t:
			{
				int ix = IntIndex(i, "кортежа");
				return (uint)ix < (uint)t.Items.Length ? t.Items[ix] : throw OutOfRange(ix, t.Items.Length);
			}
			case KObject:
				return CallOperator("[]", [o, i]);
			case KNativeBox:
				return CallOperator("[]", [o, i]);
		}
		throw NilAccess(o, "индексатор");
	}

	internal void IndexSet(KValue o, KValue i, KValue v)
	{
		switch (o.Ref)
		{
			case KListObject l:
			{ int ix = IntIndex(i, "списка"); if ((uint)ix >= (uint)l.Items.Count) throw OutOfRange(ix, l.Items.Count); l.Items[ix] = v; return; }
			case KArrayObject a:
			{ int ix = IntIndex(i, "массива"); if ((uint)ix >= (uint)a.Items.Length) throw OutOfRange(ix, a.Items.Length); a.Items[ix] = v; return; }
			case KDictObject d:
				d.Items[i] = v; return;
			case KObject:
				CallOperator("[]", [o, i, v]); return;
		}
		throw NilAccess(o, "индексатор");
	}

	private static (int Start, int Length) SliceBounds(KRange r, int len)
	{
		int start = r.Start.IsNilOrNone ? 0 : (int)r.Start.Bits;
		int end = r.End.IsNilOrNone ? len : (int)r.End.Bits + (r.Inclusive ? 1 : 0);
		if (start < 0 || end > len || start > end) throw new KernPanic($"срез {start}..{end} вне диапазона (длина {len})");
		return (start, end - start);
	}

	/// <summary>`item in container`.</summary>
	internal bool Contains(KValue item, KValue container)
	{
		if (container.Kind == ValueKind.Str)
			return item.Kind == ValueKind.Char ? container.AsStr.Contains(item.AsChar)
				 : item.Kind == ValueKind.Str ? container.AsStr.Contains(item.AsStr, StringComparison.Ordinal)
				 : throw new KernPanic("в строке можно искать только str или char");
		switch (container.Ref)
		{
			case KRange r: return r.Contains(item);
			case KListObject l: return l.Items.Any(x => ValueOps.StructuralEquals(x, item));
			case KArrayObject a: return a.Items.Any(x => ValueOps.StructuralEquals(x, item));
			case KDictObject d: return d.Items.ContainsKey(item);
			case KTupleObject t: return t.Items.Any(x => ValueOps.StructuralEquals(x, item));
			case KObject or KNativeBox: return CallOperator("in", [container, item]).AsBool;
		}
		throw NilAccess(container, "оператор in");
	}

	/// <summary>Длина list/array/tuple/str либо -1.</summary>
	private static int LengthOf(KValue v)
	{
		if (v.Kind == ValueKind.Str) return v.AsStr.Length;
		return v.Ref switch
		{
			KListObject l => l.Items.Count, KArrayObject a => a.Items.Length, KTupleObject t => t.Items.Length, KDictObject d => d.Items.Count, _ => -1,
		};
	}

	// ------------------------------------------------------------ деструктуризация

	/// <summary>Раскладывает значение: на стек ложатся count значений, ПЕРВЫЙ элемент на вершине. flags = позиция rest + 1 либо 0.</summary>
	private int DestructureInto(KValue v, int count, int flags, KValue[] regs, int sp)
	{
		KValue[] items = v.Ref switch
		{
			KTupleObject t => t.Items,
			KListObject l => [.. l.Items],
			KArrayObject a => a.Items,
			_ => throw NilAccess(v, "деструктуризация"),
		};
		var elemType = v.Ref is KListObject lo ? lo.ElementType : v.Ref is KArrayObject ao ? ao.ElementType : TypeFactory.Object;
		var result = new KValue[count];
		if (flags == 0)
		{
			if (items.Length != count) throw new KernPanic($"деструктуризация: ожидалось {count} элементов, получено {items.Length}");
			Array.Copy(items, result, count);
		}
		else
		{
			int restPos = flags - 1;
			int after = count - 1 - restPos;
			if (items.Length < restPos + after) throw new KernPanic($"деструктуризация: ожидалось не менее {restPos + after} элементов, получено {items.Length}");
			Array.Copy(items, 0, result, 0, restPos);
			int restLen = items.Length - restPos - after;
			result[restPos] = KValue.Obj(new KListObject(elemType, [.. items.Skip(restPos).Take(restLen)]));
			Array.Copy(items, items.Length - after, result, restPos + 1, after);
		}
		for (int i = count - 1; i >= 0; i--) regs[sp++] = result[i];
		return sp;
	}

	// ------------------------------------------------------------ enum, unit

	/// <summary>Значение члена enum (кэшируется, ссылочное равенство).</summary>
	public KValue GetEnumValue(int typeIdx, int memberIdx) => EnumValue(typeIdx, memberIdx);

	private KValue EnumValue(int typeIdx, int memberIdx)
	{
		var info = Info.Classes[typeIdx];
		// кэш общий для потоков (общая память): создание и чтение — под замком, чтобы значение было единственным
		lock (info)
		{
			var cache = info.EnumValuesCache ??= [];
			if (cache.TryGetValue(memberIdx, out var cached)) return KValue.Obj(cached);
			var def = info.Def;
			var ct = Types.Class(typeIdx, []);
			var fields = new KValue[Math.Max(1, info.FieldCount)];
			fields[0] = KValue.I64(def.EnumValues[memberIdx]);
			var obj = new KObject(ct, fields);
			cache[memberIdx] = obj;
			return KValue.Obj(obj);
		}
	}

	private KValue CopyUnitValue(KValue v)
	{
		if (v.Ref is not KObject o || !Info.Classes[o.Type.DefIndex].IsValueType) return v;
		var copy = o.CopyUnit();
		for (int i = 0; i < copy.Fields.Length; i++) copy.Fields[i] = CopyUnitValue(copy.Fields[i]);
		return KValue.Obj(copy);
	}

	// ------------------------------------------------------------ динамический доступ по имени

	private KValue GetPropDynamic(KValue v, string name)
	{
		switch (v.Ref)
		{
			case KObject o:
			{
				var info = Info.Classes[o.Type.DefIndex];
				if (info.PropsByName.TryGetValue(name, out var p) && p.Getter >= 0)
					return InvokeFunction(p.Getter, [], v, true, [], ClassArgsFor(p.Getter, o), []);
				if (info.FieldsByName.TryGetValue(name, out var f)) return o.Fields[f.Slot];
				break;
			}
			case KTupleObject t:
			{
				int idx = t.Names is null ? -1 : Array.IndexOf(t.Names, name);
				if (idx >= 0) return t.Items[idx];
				break;
			}
		}
		if (v.IsNilOrNone) throw NilAccess(v, $"свойство {name}");
		throw new KernPanic($"у значения типа {TypeOf(v).Name} нет свойства '{name}'");
	}

	private void SetPropDynamic(KValue target, string name, KValue value)
	{
		if (target.Ref is KObject o)
		{
			var info = Info.Classes[o.Type.DefIndex];
			if (info.PropsByName.TryGetValue(name, out var p) && (p.Setter >= 0 || p.Init >= 0))
			{
				int fn = p.Setter >= 0 ? p.Setter : p.Init;
				InvokeFunction(fn, [value], target, true, [], ClassArgsFor(fn, o), []);
				return;
			}
			if (info.FieldsByName.TryGetValue(name, out var f)) { o.Fields[f.Slot] = value; return; }
		}
		if (target.IsNilOrNone) throw NilAccess(target, $"свойство {name}");
		throw new KernPanic($"у значения типа {TypeOf(target).Name} нет изменяемого свойства '{name}'");
	}

	/// <summary>`obj.method` без вызова: функция с привязанным получателем.</summary>
	private KValue BindMethodDynamic(KValue v, string name)
	{
		if (v.Ref is KObject o)
		{
			var info = Info.Classes[o.Type.DefIndex];
			if (info.MethodsByName.TryGetValue(name, out var list) && list.Count > 0)
			{
				int fi = list[0];
				return KValue.Obj(new KFunction { Def = _funcs[fi], FuncIndex = fi, BoundThis = v, HasThis = true, ClassArgs = ClassArgsFor(fi, o) });
			}
		}
		for (int i = 0; i < _funcs.Length; i++)
		{
			var fd = _funcs[i];
			if (fd.Name == name && fd.Flags.HasFlag(MemberFlags.Extension) && fd.ExtensionOf is { } sig && IsInstance(v, Types.Resolve(sig, [], [])))
				return KValue.Obj(new KFunction { Def = fd, FuncIndex = i, BoundThis = v, HasThis = true });
		}
		if (v.IsNilOrNone) throw NilAccess(v, $"метод {name}");
		throw new KernPanic($"у значения типа {TypeOf(v).Name} нет метода '{name}'");
	}
}
