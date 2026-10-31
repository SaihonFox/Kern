using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Async;
using Kern.Vm.Values;

namespace Kern.Runtime.Tasks;

/// <summary>
/// Асинхронный канал внутри одной VM с буфером и обратным давлением: `send` ждёт свободного места, `recv` — значения.
/// Ёмкость 0 — рандеву (отправитель ждёт получателя). После `close()` буфер дочитывается, затем `recv` даёт nil.
/// Для `async loop` — `use kern.pipes[pipe_stream]` и `async loop var x in pipe_stream(p) { … }`.
/// Не разделяется между потоками (для этого есть `channel`).
/// </summary>
[KernType("pipe")]
public sealed class KernPipe<T>
{
	private readonly Queue<KValue> _buffer = new();
	private readonly Queue<KTask> _receivers = new();
	private readonly Queue<(KValue Value, KTask Done)> _senders = new();
	private readonly int _capacity;
	private bool _closed;

	[KernCtor] public KernPipe() : this(0) { }
	[KernCtor] public KernPipe(int capacity) => _capacity = System.Math.Max(0, capacity);

	[KernProperty] public int Len => _buffer.Count;
	[KernProperty] public int Capacity => _capacity;
	[KernProperty] public bool IsClosed => _closed;

	private static KTaskRef<bool> Sent(bool ok) => new(KTask.FromResult(KValue.Bool(ok)));
	private static KTaskNullable<T> Got(KValue v) => new(KTask.FromResult(v));

	/// <summary>Отправить значение; задача завершается, когда оно принято (true) либо канал закрыт (false).</summary>
	[KernMember]
	public KTaskRef<bool> Send(T value)
	{
		var v = Kv.Of(value);
		if (_closed) return Sent(false);
		while (_receivers.Count > 0)
		{
			var r = _receivers.Dequeue();
			if (r.IsCompleted) continue;
			r.Complete(v);
			return Sent(true);
		}
		if (_buffer.Count < _capacity)
		{
			_buffer.Enqueue(v);
			return Sent(true);
		}
		var done = new KTask();
		_senders.Enqueue((v, done));
		return new KTaskRef<bool>(done);
	}

	/// <summary>Получить значение; nil — канал закрыт и пуст.</summary>
	[KernMember]
	public KTaskNullable<T> Recv()
	{
		if (_buffer.Count > 0)
		{
			var v = _buffer.Dequeue();
			RefillFromSender();
			return Got(v);
		}
		while (_senders.Count > 0)
		{
			var (value, done) = _senders.Dequeue();
			if (done.IsCompleted) continue;
			done.Complete(KValue.True);
			return Got(value);
		}
		if (_closed) return Got(KValue.Nil);
		var waiting = new KTask();
		_receivers.Enqueue(waiting);
		return new KTaskNullable<T>(waiting);
	}

	/// <summary>Взять значение из буфера без ожидания; nil — сейчас пусто.</summary>
	[KernMember]
	public T? TryRecv()
	{
		if (_buffer.Count == 0) return Kv.As<T?>(KValue.Nil);
		var v = _buffer.Dequeue();
		RefillFromSender();
		return Kv.As<T?>(v);
	}

	/// <summary>Закрыть: ждущие получатели получают nil, ждущие отправители — false; буфер остаётся для дочитывания.</summary>
	[KernMember]
	public void Close()
	{
		if (_closed) return;
		_closed = true;
		while (_receivers.Count > 0) _receivers.Dequeue().Complete(KValue.Nil);
		while (_senders.Count > 0) _senders.Dequeue().Done.Complete(KValue.False);
	}

	private void RefillFromSender()
	{
		while (_senders.Count > 0)
		{
			var (value, done) = _senders.Dequeue();
			if (done.IsCompleted) continue;
			_buffer.Enqueue(value);
			done.Complete(KValue.True);
			return;
		}
	}
}
