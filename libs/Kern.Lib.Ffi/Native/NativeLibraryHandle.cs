using System.Runtime.InteropServices;
using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Lib.Ffi.Native;

/// <summary>
/// Загруженная C/C++ библиотека (<c>native_lib lib("sqlite3")</c>).
/// </summary>
[KernType("native_lib")]
public sealed class KernNativeLib : IDisposable
{
	private readonly nint _handle;
	private bool _closed;

	internal KernNativeLib(string name, nint handle)
	{
		Name = name;
		_handle = handle;
	}

	public string Name { get; }

	internal nint Handle => _handle;

	internal nint Export(string symbol)
	{
		if (!NativeLibrary.TryGetExport(_handle, symbol, out nint address))
			throw new KernPanic($"ffi: в библиотеке '{Name}' нет функции '{symbol}'");
		return address;
	}

	internal bool HasExport(string symbol) =>
		NativeLibrary.TryGetExport(_handle, symbol, out _);

	public void Dispose()
	{
		if (_closed)
			return;
		_closed = true;
		NativeLibrary.Free(_handle);
	}

	internal static KernNativeLib Open(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			throw new KernPanic("ffi: имя библиотеки не может быть пустым");
		foreach (string candidate in Candidates(name))
		{
			if (NativeLibrary.TryLoad(candidate, out nint handle))
				return new KernNativeLib(name, handle);
		}

		throw new KernPanic(
			$"ffi: не удалось загрузить библиотеку '{name}' (пробовали: {string.Join(", ", Candidates(name))})");
	}

	private static IEnumerable<string> Candidates(string name)
	{
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string candidate in RawCandidates(name))
		{
			if (seen.Add(candidate))
				yield return candidate;
		}
	}

	private static IEnumerable<string> RawCandidates(string name)
	{
		if (File.Exists(name))
			yield return Path.GetFullPath(name);
		yield return name;
		if (Path.HasExtension(name))
			yield break;
		yield return name + ".dll";
		yield return "lib" + name + ".dll";
		yield return name + ".so";
		yield return "lib" + name + ".so";
		yield return "lib" + name + ".so.6";
		yield return name + ".dylib";
		yield return "lib" + name + ".dylib";
	}

	internal static KernNativeLib Require(KNativeBox self) => self.Value switch
	{
		KernNativeLib library => library,
		_ => throw new KernPanic($"native_lib: ожидалась библиотека, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Члены типа <c>native_lib</c>.</summary>
[KernType("native_lib")]
public static class NativeLibApi
{
	[KernCtor]
	[KernName("native_lib")]
	public static KernNativeLib Open(string name) => KernNativeLib.Open(name);

	[KernProperty]
	public static string Name(KNativeBox self) => KernNativeLib.Require(self).Name;

	/// <summary><c>lib.fn("strlen", "i64(str)")</c>: найти символ и разобрать сигнатуру.</summary>
	[KernMember]
	public static KernNativeFn Fn(KNativeBox self, string symbol, string signature)
	{
		KernNativeLib library = KernNativeLib.Require(self);
		NativeSignature parsed = NativeSignature.Parse(signature);
		return new KernNativeFn(symbol, library.Export(symbol), parsed);
	}

	[KernMember]
	public static bool Has(KNativeBox self, string symbol) =>
		KernNativeLib.Require(self).HasExport(symbol);

	[KernMember]
	public static void Close(KNativeBox self) => KernNativeLib.Require(self).Dispose();
}
