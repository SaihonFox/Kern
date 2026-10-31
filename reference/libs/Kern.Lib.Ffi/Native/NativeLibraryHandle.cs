using System.Runtime.InteropServices;
using Kern.Binding.Attributes;

namespace Kern.Lib.Ffi.Native;

/// <summary>
/// Загруженная нативная библиотека (C/C++): <c>native_lib lib("sqlite3")</c>, затем <c>lib.fn("sqlite3_libversion", "str()")</c>.
/// Имя без расширения и префикса ищется во всех привычных вариантах платформы (<c>x.dll</c>, <c>libx.so</c>, <c>libx.dylib</c>).
/// </summary>
[KernType("native_lib")]
public sealed class KernNativeLib : IDisposable
{
	private readonly nint _handle;
	public string Name { get; }

	[KernCtor] public KernNativeLib(string name)
	{
		Name = name;
		_handle = Open(name) ?? throw new InvalidOperationException($"не удалось загрузить нативную библиотеку '{name}'");
	}

	private static nint? Open(string name)
	{
		foreach (var candidate in Candidates(name))
			if (NativeLibrary.TryLoad(candidate, out var h)) return h;
		return null;
	}

	private static IEnumerable<string> Candidates(string name)
	{
		yield return name;
		if (Path.HasExtension(name) && !name.EndsWith(".so", StringComparison.Ordinal)) yield break;
		yield return name + ".dll";
		yield return "lib" + name + ".so";
		yield return name + ".so";
		yield return "lib" + name + ".dylib";
		yield return name + ".dylib";
	}

	/// <summary>
	/// Функция библиотеки. Сигнатура: <c>"возврат(аргументы)"</c>, типы: void, bool, i8, i16, i32, i64, u8, u16, u32, u64, f32, f64,
	/// ptr (указатель/handle как число), str (UTF-8 строка). Пример: <c>"i32(i32, str)"</c>. Конвенция вызова — cdecl.
	/// </summary>
	[KernMember] public KernNativeFn Fn(string symbol, string signature)
	{
		if (!NativeLibrary.TryGetExport(_handle, symbol, out var address))
			throw new InvalidOperationException($"в библиотеке '{Name}' нет функции '{symbol}'");
		return new KernNativeFn(symbol, address, NativeSignature.Parse(signature));
	}

	[KernMember] public bool Has(string symbol) => NativeLibrary.TryGetExport(_handle, symbol, out _);

	public void Dispose() => NativeLibrary.Free(_handle);
	[KernMember] public void Close() => Dispose();
}
