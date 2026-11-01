using Kern.Binding.Attributes;

namespace Kern.Lib.Backend;

/// <summary>Модуль <c>kern.backend</c>: типы и функции backend-сервера.</summary>
[KernModule("kern.backend")]
public static class BackendModule
{
	[KernMember]
	public static string Version() => "0.1.0";
}

/// <summary>
/// Модуль <c>kern.backend.server</c> (spec round 21): в него вынесен цикл сервера.
/// Сам <c>serve()</c> объявлен членом <c>app</c>; модуль существует для <c>use</c>.
/// </summary>
[KernModule("kern.backend.server")]
public static class BackendServerModule
{
	[KernMember]
	public static string Version() => "0.1.0";
}
