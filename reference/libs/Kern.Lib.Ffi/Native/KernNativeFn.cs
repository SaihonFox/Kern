using System.Globalization;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Kern.Binding.Attributes;
using Kern.Binding.Marshalling;
using Kern.Binding.Wrappers;

namespace Kern.Lib.Ffi.Native;

/// <summary>Функция из нативной библиотеки. Вызов собирается один раз через `calli` (DynamicMethod), без P/Invoke-заглушек на каждый тип.</summary>
[KernType("native_fn")]
public sealed class KernNativeFn
{
	private delegate object? Caller(nint address, object?[] args);

	private readonly string _name;
	private readonly nint _address;
	private readonly NativeSignature _sig;
	private readonly Caller _caller;

	internal KernNativeFn(string name, nint address, NativeSignature sig)
	{
		_name = name;
		_address = address;
		_sig = sig;
		_caller = Build(sig);
	}

	[KernProperty] public string Name => _name;

	/// <summary>Вызвать функцию. Числа/bool/str передаются по сигнатуре; результат — число, bool, str или nil (void).</summary>
	[KernMember] public object? Call(KList<object> args)
	{
		if (args.Count != _sig.Params.Length)
			throw new ArgumentException($"{_name}: ожидается аргументов {_sig.Params.Length}, передано {args.Count}");
		var native = new object?[args.Count];
		var toFree = new List<nint>();
		try
		{
			for (int i = 0; i < native.Length; i++)
				native[i] = ToNative(Marshaller.ToObject(args[i]), _sig.Params[i], toFree);
			return FromNative(_caller(_address, native), _sig.Return);
		}
		finally
		{
			foreach (var p in toFree) Marshal.FreeCoTaskMem(p);
		}
	}

	private static object ToNative(object? v, NativeKind kind, List<nint> toFree)
	{
		var inv = CultureInfo.InvariantCulture;
		switch (kind)
		{
			case NativeKind.Str:
			{
				if (v is null) return (nint)0;
				nint p = Marshal.StringToCoTaskMemUTF8(Convert.ToString(v, inv));
				toFree.Add(p);
				return p;
			}
			case NativeKind.Ptr: return v is null ? (nint)0 : (nint)Convert.ToInt64(v, inv);
			case NativeKind.Bool: return (byte)(v is bool b ? (b ? 1 : 0) : Convert.ToInt64(v, inv) != 0 ? 1 : 0);
			default: return Convert.ChangeType(v ?? 0, NativeSignature.Clr(kind), inv);
		}
	}

	private static object? FromNative(object? raw, NativeKind kind) => kind switch
	{
		NativeKind.Void => null,
		NativeKind.Bool => (byte)raw! != 0,
		NativeKind.Str => (nint)raw! == 0 ? null : Marshal.PtrToStringUTF8((nint)raw!),
		NativeKind.Ptr => (long)(nint)raw!,
		NativeKind.U64 => unchecked((long)(ulong)raw!),
		NativeKind.F32 => (double)(float)raw!,
		NativeKind.F64 => raw,
		_ => Convert.ToInt64(raw, CultureInfo.InvariantCulture),
	};

	private static Caller Build(NativeSignature sig)
	{
		var clrParams = sig.Params.Select(NativeSignature.Clr).ToArray();
		var ret = NativeSignature.Clr(sig.Return);
		var dm = new DynamicMethod("ffi_call", typeof(object), [typeof(nint), typeof(object[])], typeof(KernNativeFn).Module, skipVisibility: true);
		var il = dm.GetILGenerator();
		for (int i = 0; i < clrParams.Length; i++)
		{
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Ldc_I4, i);
			il.Emit(OpCodes.Ldelem_Ref);
			il.Emit(OpCodes.Unbox_Any, clrParams[i]);
		}
		il.Emit(OpCodes.Ldarg_0);
		il.EmitCalli(OpCodes.Calli, CallingConvention.Cdecl, ret, clrParams);
		if (ret == typeof(void)) il.Emit(OpCodes.Ldnull);
		else il.Emit(OpCodes.Box, ret);
		il.Emit(OpCodes.Ret);
		return (Caller)dm.CreateDelegate(typeof(Caller));
	}
}
