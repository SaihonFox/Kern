using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Tasks;

/// <summary>
/// Токен отмены (spec round 23): источник и токен в одном объекте. Отмена кооперативная —
/// <c>tok.check()</c> паникует, <c>tok.delay(ms)</c> прерывается. Дочерний токен
/// отменяется вместе с родителем.
/// </summary>
[KernType("cancel_token")]
public sealed class KernCancelToken : IDisposable
{
	private readonly CancellationTokenSource _source;
	private readonly List<CancellationTokenRegistration> _registrations = [];

	public KernCancelToken()
	{
		_source = new CancellationTokenSource();
	}

	public KernCancelToken(KernCancelToken parent) : this()
	{
		// Каскад: отмена родителя отменяет ребёнка.
		_registrations.Add(parent._source.Token.Register(Cancel));
	}

	public bool IsCancelled => _source.IsCancellationRequested;

	public void Cancel()
	{
		if (_source.IsCancellationRequested)
			return;
		_source.Cancel();
	}

	public void CancelAfter(int milliseconds)
	{
		if (_source.IsCancellationRequested)
			return;
		_source.CancelAfter(System.Math.Max(0, milliseconds));
	}

	/// <summary>Паника «операция отменена», если токен уже отменён.</summary>
	public void Check()
	{
		if (_source.IsCancellationRequested)
			throw new KernPanic("операция отменена");
	}

	/// <summary>Пауза, прерываемая отменой.</summary>
	public Task Delay(int milliseconds) =>
		Task.Delay(System.Math.Max(0, milliseconds), _source.Token);

	/// <summary>Завершится, когда токен отменён.</summary>
	public Task WhenCancelled()
	{
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		if (_source.IsCancellationRequested)
			completion.TrySetResult();
		else
			_registrations.Add(_source.Token.Register(() => completion.TrySetResult()));
		return completion.Task;
	}

	public void RegisterCancellation(Action action) =>
		_registrations.Add(_source.Token.Register(action));

	public void Dispose()
	{
		foreach (CancellationTokenRegistration registration in _registrations)
			registration.Dispose();
		_registrations.Clear();
		_source.Dispose();
	}
}

/// <summary>Фабрика и члены <c>cancel_token</c>.</summary>
[KernType("cancel_token")]
public static class CancelTokenApi
{
	[KernCtor]
	[KernName("cancel_token")]
	[KernReturn("cancel_token")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernCancelToken()));

	/// <summary><c>child()</c>: дочерний токен отменяется вместе с этим.</summary>
	[KernCtor]
	[KernName("cancel_token")]
	[KernReturn("cancel_token")]
	public static KValue CreateChild(KValue parent) => KValue.Obj(new KNativeBox(new KernCancelToken(RequireValue(parent))));

	[KernProperty]
	public static bool Cancelled(KNativeBox self) => Require(self).IsCancelled;

	[KernMember]
	public static void Cancel(KNativeBox self) => Require(self).Cancel();

	[KernName("cancel_after")]
	[KernMember]
	public static void CancelAfter(KNativeBox self, int milliseconds) => Require(self).CancelAfter(milliseconds);

	[KernMember]
	public static void Check(KNativeBox self) => Require(self).Check();

	/// <summary><c>child()</c>: отдельный токен, отменяемый вместе с родителем.</summary>
	[KernMember]
	[KernReturn("cancel_token")]
	public static KValue Child(KNativeBox self) => KValue.Obj(new KNativeBox(new KernCancelToken(Require(self))));

	/// <summary><c>await tok.delay(ms)</c>: пауза, прерываемая отменой.</summary>
	[KernMember]
	[KernReturn("task<void>")]
	public static Task Delay(KNativeBox self, int milliseconds) => Require(self).Delay(milliseconds);

	/// <summary><c>await tok.wait()</c>: ждать отмены.</summary>
	[KernMember]
	[KernReturn("task<void>")]
	public static Task Wait(KNativeBox self) => Require(self).WhenCancelled();

	[KernName("on_cancel")]
	[KernMember]
	public static void OnCancel(Interpreter interpreter, KNativeBox self, KValue callback) =>
		Require(self).RegisterCancellation(() => interpreter.Scheduler.Post(() => interpreter.RunValue(callback)));

	[KernMember]
	public static void Dispose(KNativeBox self) => Require(self).Dispose();

	internal static KernCancelToken Require(KNativeBox self) => self.Value switch
	{
		KernCancelToken token => token,
		_ => throw new KernPanic($"cancel_token: ожидался токен отмены, получено {self.Value.GetType().Name}"),
	};

	internal static KernCancelToken RequireValue(KValue value) =>
		value.Ref is KNativeBox { Value: KernCancelToken token }
			? token
			: throw new KernPanic($"cancel_token: ожидался токен отмены, получено {value.TypeName}");
}
