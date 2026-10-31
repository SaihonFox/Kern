using Kern.Vm.Execution;

namespace Kern.Vm.Async;

/// <summary>Источник отмены (cancellation): Cancel() будит ожидающих; токен передаётся в нативные async-операции.</summary>
public sealed class KCancellation : IDisposable
{
	private readonly CancellationTokenSource _cts;

	public KCancellation() => _cts = new CancellationTokenSource();

	/// <summary>Связанный источник: отменяется вместе с родителем.</summary>
	public KCancellation(KCancellation parent) => _cts = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);

	public CancellationToken Token => _cts.Token;
	public bool IsCancelled => _cts.IsCancellationRequested;

	public void Cancel() => _cts.Cancel();
	public void CancelAfter(int milliseconds) => _cts.CancelAfter(milliseconds);

	public void ThrowIfCancelled()
	{
		if (IsCancelled) throw new KernPanic("операция отменена");
	}

	public IDisposable Register(Action callback) => _cts.Token.Register(callback);

	public void Dispose() => _cts.Dispose();
}
