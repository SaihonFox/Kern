using System.Runtime.InteropServices;
using Kern.Lib.Ffi.Native;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>FFI: загрузка C-библиотеки, поиск функции, сигнатуры и вызов cdecl.</summary>
public sealed class FfiTests
{
	private static string PlatformLibrary() =>
		RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
			? "msvcrt.dll"
			: RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
				? "/usr/lib/libSystem.B.dylib"
				: "libc.so.6";

	[Fact]
	public void Strlen_returns_utf8_byte_length()
	{
		using KernNativeLib library = NativeLibApi.Open(PlatformLibrary());
		Assert.True(NativeLibApi.Has(new KNativeBox(library), "strlen"));

		KernNativeFn function = NativeLibApi.Fn(new KNativeBox(library), "strlen", "i64(str)");
		KValue result = NativeFnApi.Call(
			new KNativeBox(function),
			KValue.Obj(new Kern.Vm.Values.KList(null, [KValue.Str("Привет")])));

		Assert.Equal(12L, result.AsI64);
	}

	[Fact]
	public void Abs_reads_and_writes_i32()
	{
		using KernNativeLib library = NativeLibApi.Open(PlatformLibrary());
		KernNativeFn function = NativeLibApi.Fn(new KNativeBox(library), "abs", "i32(i32)");

		KValue result = NativeFnApi.Call(
			new KNativeBox(function),
			KValue.Obj(new Kern.Vm.Values.KList(null, [KValue.I32(-42)])));

		Assert.Equal(42, result.AsI32);
	}

	[Fact]
	public void Atof_returns_f64()
	{
		using KernNativeLib library = NativeLibApi.Open(PlatformLibrary());
		KernNativeFn function = NativeLibApi.Fn(new KNativeBox(library), "atof", "f64(str)");

		KValue result = NativeFnApi.Call(
			new KNativeBox(function),
			KValue.Obj(new Kern.Vm.Values.KList(null, [KValue.Str("1.5")])));

		Assert.Equal(1.5, result.AsF64);
	}

	[Fact]
	public void Unknown_symbol_and_bad_signature_are_panics()
	{
		using KernNativeLib library = NativeLibApi.Open(PlatformLibrary());
		Assert.False(NativeLibApi.Has(new KNativeBox(library), "no_such_kern_symbol"));

		Assert.Throws<KernPanic>(() =>
			NativeLibApi.Fn(new KNativeBox(library), "no_such_kern_symbol", "void()"));
		Assert.Throws<KernPanic>(() =>
			NativeLibApi.Fn(new KNativeBox(library), "strlen", "wat(str)"));
		Assert.Throws<KernPanic>(() =>
			NativeLibApi.Fn(new KNativeBox(library), "strlen", "i64(void)"));
	}

	[Fact]
	public void Wrong_argument_count_is_a_panic()
	{
		using KernNativeLib library = NativeLibApi.Open(PlatformLibrary());
		KernNativeFn function = NativeLibApi.Fn(new KNativeBox(library), "strlen", "i64(str)");

		Assert.Throws<KernPanic>(() =>
			NativeFnApi.Call(
				new KNativeBox(function),
				KValue.Obj(new Kern.Vm.Values.KList(null, []))));
	}

	[Fact]
	public void Function_exposes_name_and_signature()
	{
		using KernNativeLib library = NativeLibApi.Open(PlatformLibrary());
		KernNativeFn function = NativeLibApi.Fn(new KNativeBox(library), "strlen", "i64(str)");

		Assert.Equal("strlen", NativeFnApi.Name(new KNativeBox(function)));
		Assert.Equal("i64(str)", NativeFnApi.Signature(new KNativeBox(function)));
	}
}
