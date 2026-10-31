namespace Kern.Binding.Values;

/// <summary>
/// Типизированный `result&lt;T,E&gt;` для нативных привязок. В VM результат — это
/// union-значение `Ok`/`Err`; обёртка нужна C#-коду, который ещё не работает с VM-объектами.
/// </summary>
public readonly struct KResult<T, E>
{
	private readonly T? _ok;
	private readonly E? _error;

	private KResult(bool isOk, T? ok, E? error)
	{
		IsOk = isOk;
		_ok = ok;
		_error = error;
	}

	public bool IsOk { get; }

	public bool IsErr => !IsOk;

	public T Ok => IsOk
		? _ok!
		: throw new InvalidOperationException("KResult: результат не является Ok");

	public E Err => !IsOk
		? _error!
		: throw new InvalidOperationException("KResult: результат не является Err");

	public static KResult<T, E> Success(T value) => new(true, value, default);

	public static KResult<T, E> Failure(E error) => new(false, default, error);

	public TResult Match<TResult>(Func<T, TResult> ok, Func<E, TResult> err)
	{
		ArgumentNullException.ThrowIfNull(ok);
		ArgumentNullException.ThrowIfNull(err);
		return IsOk ? ok(_ok!) : err(_error!);
	}

	public override string ToString() => IsOk ? $"Ok({_ok})" : $"Err({_error})";
}
