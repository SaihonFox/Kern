namespace Kern.Vm.Values;

/// <summary>
/// Состояние динамического прокси (моки тестового фреймворка, Refit-подобные клиенты):
/// хранится в <c>KObject.Native</c>; вызов абстрактного/интерфейсного метода, у которого нет реализации, уходит в <see cref="Handler"/>.
/// </summary>
public sealed class KProxyState(Func<Execution.Interpreter, int, KValue[], KValue> handler)
{
	public Func<Execution.Interpreter, int, KValue[], KValue> Handler { get; } = handler;

	/// <summary>Пользовательские данные прокси (журнал вызовов, настройки).</summary>
	public object? Tag { get; set; }

	public KValue Invoke(Execution.Interpreter vm, int functionIndex, KValue[] args) => Handler(vm, functionIndex, args);
}
