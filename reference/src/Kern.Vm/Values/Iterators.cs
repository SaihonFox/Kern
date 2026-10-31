using Kern.Vm.Execution;

namespace Kern.Vm.Values;

/// <summary>Итератор цикла loop/for. MoveNext кладёт значение в Current.</summary>
public abstract class KIterator
{
	public KValue Current;
	public abstract bool MoveNext();
}

public sealed class ListIterator(List<KValue> items) : KIterator
{
	private int _i;
	public override bool MoveNext()
	{
		if (_i >= items.Count) return false;
		Current = items[_i++];
		return true;
	}
}

public sealed class ArrayIterator(KValue[] items) : KIterator
{
	private int _i;
	public override bool MoveNext()
	{
		if (_i >= items.Length) return false;
		Current = items[_i++];
		return true;
	}
}

public sealed class StrIterator(string s) : KIterator
{
	private int _i;
	public override bool MoveNext()
	{
		if (_i >= s.Length) return false;
		Current = KValue.Char(s[_i++]);
		return true;
	}
}

/// <summary>Итерация словаря: пары (key, value) как кортежи. Снимок ключей — безопасно к изменениям.</summary>
public sealed class DictIterator(KDictObject dict) : KIterator
{
	private readonly KeyValuePair<KValue, KValue>[] _snap = dict.Items.ToArray();
	private int _i;
	public override bool MoveNext()
	{
		if (_i >= _snap.Length) return false;
		var kv = _snap[_i++];
		Current = KValue.Obj(new KTupleObject([kv.Key, kv.Value], ["key", "value"]));
		return true;
	}
}

public sealed class RangeIterator : KIterator
{
	private readonly ValueKind _kind;
	private readonly bool _isChar;
	private long _cur;
	private readonly long _end;
	private readonly long _step;
	private readonly bool _inclusive;

	public RangeIterator(KRange r)
	{
		_isChar = r.Start.Kind == ValueKind.Char;
		_kind = _isChar ? ValueKind.Char : r.Start.IsInteger ? r.Start.Kind : ValueKind.I64;
		if (r.Start.IsFloat || r.End.IsFloat) throw new KernPanic("диапазон по вещественным числам не итерируется");
		_cur = r.Start.Bits;
		_end = r.End.Bits;
		_step = r.HasStep ? r.Step.Bits : 1;
		_inclusive = r.Inclusive;
		if (_step == 0) throw new KernPanic("шаг диапазона равен нулю");
		if (_step < 0 && !r.HasStep) _step = 1;
	}

	public override bool MoveNext()
	{
		bool ok = _step > 0 ? (_inclusive ? _cur <= _end : _cur < _end) : (_inclusive ? _cur >= _end : _cur > _end);
		if (!ok) return false;
		Current = Num.Wrap(_kind, _cur);
		_cur += _step;
		return true;
	}
}

public sealed class TupleIterator(KValue[] items) : KIterator
{
	private int _i;
	public override bool MoveNext()
	{
		if (_i >= items.Length) return false;
		Current = items[_i++];
		return true;
	}
}

/// <summary>Итерация произвольной CLR-последовательности (нативные типы).</summary>
public sealed class ClrIterator(System.Collections.IEnumerator e) : KIterator
{
	public override bool MoveNext()
	{
		if (!e.MoveNext()) return false;
		Current = Num.FromClr(e.Current);
		return true;
	}
}

/// <summary>Пользовательский итератор: объект с методами has_next()/next().</summary>
public sealed class UserIterator(Interpreter vm, KObject obj, int hasNext, int next) : KIterator
{
	public override bool MoveNext()
	{
		if (!vm.CallMethod(obj, hasNext, []).AsBool) return false;
		Current = vm.CallMethod(obj, next, []);
		return true;
	}
}
