using Kern.Binding.Attributes;

namespace Kern.Lib.Di;

/// <summary>Модуль <c>kern.di</c>: контейнер объявлен типом <c>container</c>.</summary>
[KernModule("kern.di")]
public static class DiModule
{
	[KernMember]
	public static string Version() => "0.1.0";
}
