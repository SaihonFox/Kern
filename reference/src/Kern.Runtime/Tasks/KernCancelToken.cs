using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Vm.Async;
using Kern.Vm.Execution;

namespace Kern.Runtime.Tasks;

/// <summary>
/// Токен отмены: источник и токен в одном объекте. Отмена кооперативная — async-функция принимает токен и проверяет `tok.check()`
/// либо ждёт через `tok.delay(ms)`; дочерний токен (`tok.child()`) отменяется вместе с родителем.
/// <code>
/// async fun poll(cancel_token tok) {
///     loop { tok.check(); await tok.delay(100) }
/// }
/// </code>
/// </summary>
[KernType("cancel_token")]
public sealed class KernCancelToken : IDisposable
{
	internal KCancellation Source { get; }

	[KernCtor] public KernCancelToken() => Source = new KCancellation();
	[KernCtor] public KernCancelToken(KernCancelToken parent) => Source = new KCancellation(parent.Source);

	[KernProperty] public bool Cancelled => Source.IsCancelled;

	[KernMember] public void Cancel() => Source.Cancel();

	/// <summary>Отменить автоматически через заданное время.</summary>
	[KernMember] public void CancelAfter(int milliseconds) => Source.CancelAfter(System.Math.Max(0, milliseconds));

	/// <summary>Паника «операция отменена», если токен отменён.</summary>
	[KernMember] public void Check() => Source.ThrowIfCancelled();

	/// <summary>Токен, который отменяется вместе с этим (и может быть отменён отдельно).</summary>
	[KernMember] public KernCancelToken Child() => new(this);

	/// <summary>Пауза, прерываемая отменой: при отмене задача завершается как отменённая.</summary>
	[KernMember] public Task Delay(int milliseconds) => Task.Delay(System.Math.Max(0, milliseconds), Source.Token);

	/// <summary>Завершается, когда токен отменён.</summary>
	[KernMember]
	public Task Wait()
	{
		var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Source.Token.Register(() => tcs.TrySetResult());
		return tcs.Task;
	}

	/// <summary>Вызвать функцию при отмене (в потоке VM).</summary>
	[KernMember]
	public void OnCancel(KAction callback, [KernVm] Interpreter vm) =>
		Source.Register(() => vm.Scheduler.Post(() => callback.Invoke()));

	[KernMember] public void Dispose() => Source.Dispose();
}
