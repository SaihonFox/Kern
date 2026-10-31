using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Core;

/// <summary>Свободные функции модуля kern.core (prelude): вывод, panic, задержка, выход.</summary>
[KernModule("kern.core")]
public static class CoreApi
{
	[KernMember] public static void Print([KernVm] Interpreter vm, KValue value) => Console.Out.Write(vm.Stringify(value));
	[KernMember] public static void Println([KernVm] Interpreter vm, KValue value) => Console.Out.WriteLine(vm.Stringify(value));
	[KernMember] public static void Println() => Console.Out.WriteLine();

	/// <summary>Вывод в stderr.</summary>
	[KernMember] public static void Eprintln([KernVm] Interpreter vm, KValue value) => Console.Error.WriteLine(vm.Stringify(value));

	/// <summary>Аварийное завершение с сообщением (стек-трейс добавляет VM).</summary>
	[KernMember] public static void Panic(string message) => throw new KernPanic(message);

	/// <summary>Асинхронная пауза в миллисекундах: <c>await delay(100)</c>.</summary>
	[KernMember] public static Task Delay(int milliseconds) => Task.Delay(System.Math.Max(0, milliseconds));

	[KernMember] public static void Exit(int code = 0) => throw new KernExit(code);
}
