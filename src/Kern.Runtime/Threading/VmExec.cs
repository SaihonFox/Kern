using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Threading;

/// <summary>
/// Исполнение функции Kern в VM рабочего потока (spec round 24). Каждый рабочий поток
/// получает собственный <see cref="Interpreter.Fork"/>: общие объекты, статика и
/// замыкания, но свои фреймы и планировщик.
/// </summary>
internal static class VmExec
{
	/// <summary>Размер стека рабочих потоков (глубокие VM-кадры не растут на стеке C#).</summary>
	public const int StackSize = 16 * 1024 * 1024;

	/// <summary>Вызвать функцию-значение и дождаться результата; <c>task</c> разворачивается.</summary>
	public static KValue Run(Interpreter interpreter, KValue function, KValue[] arguments)
	{
		KValue result = interpreter.CallFunction(function, arguments);
		if (result.Ref is KTask task)
			return task.GetResult(interpreter);
		return result;
	}

	/// <summary>Текст ошибки рабочего потока для <c>result&lt;T, str&gt;</c>.</summary>
	public static string Describe(Exception error) => error switch
	{
		KernPanic panic => panic.Message,
		_ => error.Message,
	};
}
