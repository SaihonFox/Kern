using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Binding.Values;

/// <summary>Типизированная обёртка `task&lt;T&gt;` над VM-задачей.</summary>
public readonly struct KTask<T>
{
	public KTask(KValue value)
	{
		Task = value.Ref as Vm.Async.KTask
			?? throw new KernPanic($"KTask<{typeof(T).Name}>: ожидалась задача, получено {value.TypeName}");
	}

	public KTask(Vm.Async.KTask task)
	{
		Task = task;
	}

	public Vm.Async.KTask Task { get; }

	public bool IsCompleted => Task.IsCompleted;

	public bool IsSucceeded => Task.IsSucceeded;

	public T Result => Marshalling.KMarshal.To<T>(Task.Result);

	public T GetResult() => IsSucceeded
		? Result
		: throw Task.Error ?? new KernPanic("задача завершилась отменой");

	public static KTask<T> FromResult(T value) =>
		new(Vm.Async.KTask.FromResult(Marshalling.KMarshal.From(value)));

	public static KTask<T> FromError(KernPanic error) => new(Vm.Async.KTask.FromError(error));

	public KValue ToValue() => KValue.Obj(Task);

	public static implicit operator KValue(KTask<T> task) => task.ToValue();
}
