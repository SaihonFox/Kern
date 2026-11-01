using Kern.Binding.Attributes;

namespace Kern.Lib.Ffi;

/// <summary>Модуль <c>kern.ffi</c>: вызов функций из C/C++ библиотек.</summary>
[KernModule("kern.ffi")]
public static class FfiModule
{
	[KernMember]
	public static string Version() => "0.1.0";
}
