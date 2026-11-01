using Kern.Binding.Attributes;
using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Tasks;

/// <summary>
/// Асинхронный канал <c>pipe&lt;T&gt;</c> (spec round 23): <c>send</c> ждёт свободного
/// места (обратное давление), <c>recv</c> — значения. Живёт в одной VM.
/// </summary>
[KernType("pipe", GenericArity = 1)]
public sealed class KernPipe
{
	private readonly Lock _gate = new();
	private readonly Queue<KValue> _items = new();
	private readonly Queue<TaskCompletionSource<KValue>> _receivers = new();
	private readonly Queue<(KValue Value, TaskCompletionSource<bool> Accepted)> _senders = new();
	private readonly int _capacity;
	private bool _closed;

	public KernPipe(int capacity) => _capacity = System.Math.Max(1, capacity);

	public int Count
	{
		get
		{
			lock (_gate) return _items.Count;
		}
	}

	public bool IsClosed
	{
		get
		{
			lock (_gate) return _closed;
		}
	}

	/// <summary>Отправить значение; задача завершится, когда появится место.</summary>
	public Task<bool> SendAsync(KValue value)
	{
		lock (_gate)
		{
			if (_closed)
				return Task.FromResult(false);
			if (_receivers.Count > 0)
			{
				_receivers.Dequeue().TrySetResult(value);
				PumpSenders();
				return Task.FromResult(true);
			}

			if (_items.Count < _capacity)
			{
				_items.Enqueue(value);
				return Task.FromResult(true);
			}

			var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			_senders.Enqueue((value, accepted));
			return accepted.Task;
		}
	}

	/// <summary>Принять значение; <c>nil</c> после <c>close()</c> и опустошения.</summary>
	public Task<KValue> ReceiveAsync()
	{
		lock (_gate)
		{
			if (_items.Count > 0)
			{
				KValue value = _items.Dequeue();
				PumpSenders();
				return Task.FromResult(value);
			}

			if (_closed)
				return Task.FromResult(KValue.Nil);

			var receiver = new TaskCompletionSource<KValue>(TaskCreationOptions.RunContinuationsAsynchronously);
			_receivers.Enqueue(receiver);
			return receiver.Task;
		}
	}

	/// <summary>Принять значение, не блокируясь; <c>nil</c> — сейчас пусто.</summary>
	public KValue TryReceive()
	{
		lock (_gate)
		{
			if (_items.Count == 0)
				return KValue.Nil;
			KValue value = _items.Dequeue();
			PumpSenders();
			return value;
		}
	}

	/// <summary>Закрыть канал: ожидающие получат <c>nil</c>, отправители — <c>false</c>.</summary>
	public void Close()
	{
		lock (_gate)
		{
			if (_closed)
				return;
			_closed = true;
			while (_receivers.Count > 0)
				_receivers.Dequeue().TrySetResult(KValue.Nil);
			while (_senders.Count > 0)
				_senders.Dequeue().Accepted.TrySetResult(false);
		}
	}

	/// <summary>Если появилось место, принять ожидающего отправителя (под замком).</summary>
	private void PumpSenders()
	{
		if (_senders.Count == 0 || _items.Count >= _capacity)
			return;

		(KValue value, TaskCompletionSource<bool> accepted) = _senders.Dequeue();
		if (_receivers.Count > 0)
		{
			_receivers.Dequeue().TrySetResult(value);
			accepted.TrySetResult(true);
			PumpSenders();
			return;
		}

		_items.Enqueue(value);
		accepted.TrySetResult(true);
	}
}

/// <summary>Фабрика и члены <c>pipe&lt;T&gt;</c>.</summary>
[KernType("pipe", GenericArity = 1)]
public static class PipeApi
{
	[KernCtor]
	[KernName("pipe")]
	public static KValue Create(int capacity) => KValue.Obj(new KNativeBox(new KernPipe(capacity)));

	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	[KernName("is_closed")]
	[KernProperty]
	public static bool IsClosed(KNativeBox self) => Require(self).IsClosed;

	[KernMember]
	[KernReturn("task<bool>")]
	public static Task<bool> Send(KNativeBox self, KValue value) => Require(self).SendAsync(value);

	[KernMember]
	[KernReturn("task<T?>")]
	public static Task<KValue> Recv(KNativeBox self) => Require(self).ReceiveAsync();

	[KernName("try_recv")]
	[KernMember]
	[KernReturn("T?")]
	public static KValue TryRecv(KNativeBox self) => Require(self).TryReceive();

	[KernMember]
	public static void Close(KNativeBox self) => Require(self).Close();

	private static KernPipe Require(KNativeBox self) => self.Value switch
	{
		KernPipe pipe => pipe,
		_ => throw new KernPanic($"pipe: ожидался асинхронный канал, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>
/// Итератор по <c>pipe&lt;T&gt;</c> для <c>async loop</c> (spec round 23):
/// <c>pipe_stream(p)</c> возвращает <c>async_iter&lt;T&gt;</c>.
/// </summary>
[KernType("pipe_stream", GenericArity = 1, Parent = "async_iter<T>")]
public sealed class KernPipeStream(KernPipe pipe)
{
	/// <summary>Следующее значение или <c>nil</c> в конце.</summary>
	public Task<KValue> NextAsync() => pipe.ReceiveAsync();
}

/// <summary>Члены <c>pipe_stream&lt;T&gt;</c>.</summary>
[KernType("pipe_stream", GenericArity = 1, Parent = "async_iter<T>")]
public static class PipeStreamApi
{
	/// <summary><c>pipe_stream(p)</c>: результат — <c>async_iter&lt;T&gt;</c>.</summary>
	[KernCtor]
	[KernName("pipe_stream")]
	public static KValue Create<T>([KernParameterType("pipe<T>")] KValue pipe) =>
		KValue.Obj(new KNativeBox(new KernPipeStream(RequirePipe(pipe))));

	/// <summary><c>next() -&gt; task&lt;T?&gt;</c> — протокол <c>async_iter&lt;T&gt;</c>.</summary>
	[KernMember]
	[KernName("next")]
	[KernReturn("task<T?>")]
	public static Task<KValue> NextAsync(KNativeBox self) => Require(self).NextAsync();

	private static KernPipeStream Require(KNativeBox self) => self.Value switch
	{
		KernPipeStream stream => stream,
		_ => throw new KernPanic($"pipe_stream: ожидался итератор канала, получено {self.Value.GetType().Name}"),
	};

	private static KernPipe RequirePipe(KValue value) =>
		value.Ref is KNativeBox { Value: KernPipe pipe }
			? pipe
			: throw new KernPanic("pipe_stream: ожидался pipe");
}
