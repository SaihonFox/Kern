using Kern.Binding.Attributes;
using Kern.Lib.Backend.Routing;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Lib.Backend;

/// <summary>Модуль <c>kern.backend</c>: типы и функции backend-сервера.</summary>
[KernModule("kern.backend")]
public static class BackendModule
{
	[KernMember]
	public static string Version() => "0.1.0";
}

/// <summary>
/// Модуль <c>kern.backend.server</c> (spec round 21): цикл сервера.
/// Основная форма — <c>await a.serve()</c>; свободная функция <c>serve(a)</c> оставлена
/// для кода, который импортирует модуль целиком.
/// </summary>
[KernModule("kern.backend.server")]
[KernCapability("net")]
public static class BackendServerModule
{
	[KernMember]
	public static string Version() => "0.1.0";

	[KernMember]
	[KernReturn("task<void>")]
	public static Task Serve(KernApp app, Interpreter interpreter) =>
		AppApi.Serve(new KNativeBox(app), interpreter);
}
