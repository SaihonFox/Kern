using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Vm;

/// <summary>
/// Обработчик вызовов абстрактных методов прокси-объекта (spec round 9/10): объект
/// абстрактного типа создаётся без конструктора, а каждый вызов <c>@fun</c> уходит сюда.
/// Так работают <c>mock</c> и типизированный HTTP-клиент (<c>http_client.proxy&lt;T&gt;</c>).
/// </summary>
public interface IKProxyHandler
{
	/// <summary>Выполнить абстрактный метод; аргументы уже сняты со стека (self — первый).</summary>
	KValue Invoke(Interpreter interpreter, KObject receiver, FunctionDef method, KValue[] arguments);
}
