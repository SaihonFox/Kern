using System.Globalization;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Kern.Binding.Attributes;
using Kern.Binding.Marshalling;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Lib.Ffi.Native;

/// <summary>
/// Найденная функция библиотеки: <c>f.call(["abc"])</c>. Типы задаются сигнатурой
/// <c>"i64(str)"</c>; вызов идёт через <c>calli</c> с соглашением <c>cdecl</c>.
/// </summary>
[KernType("native_fn")]
public sealed class KernNativeFn
{
	private delegate object? Caller(nint address, object?[] arguments);

	private readonly string _name;
	private readonly nint _address;
	private readonly NativeSignature _signature;
	private readonly Caller _caller;

	internal KernNativeFn(string name, nint address, NativeSignature signature)
	{
		_name = name;
		_address = address;
		_signature = signature;
		_caller = Build(signature);
	}

	public string Name => _name;

	public string Signature => _signature.ToString();

	internal KValue Call(KValue value)
	{
		if (value.Ref is not Kern.Vm.Values.KList list)
			throw new KernPanic("ffi: call ожидает list аргументов");
		if (list.Count != _signature.Params.Length)
			throw new KernPanic(
				$"ffi: '{_name}' ожидает {_signature.Params.Length} аргументов, передано {list.Count}");

		var native = new object?[list.Count];
		var allocated = new List<nint>();
		try
		{
			for (var index = 0; index < list.Count; index++)
				native[index] = ToNative(list[index], _signature.Params[index], allocated);
			return FromNative(_caller(_address, native), _signature.Return);
		}
		finally
		{
			foreach (nint pointer in allocated)
				Marshal.FreeCoTaskMem(pointer);
		}
	}

	private static object ToNative(KValue value, NativeKind kind, List<nint> allocated)
	{
		CultureInfo invariant = CultureInfo.InvariantCulture;
		switch (kind)
		{
			case NativeKind.Str:
			{
				if (value.IsNilOrNone)
					return (nint)0;
				string text = value.Kind == ValueKind.Str
					? value.AsStr
					: value.Kind == ValueKind.Char
						? value.AsChar.ToString()
						: Convert.ToString(KMarshal.ToObject(value), invariant) ?? "";
				nint pointer = Marshal.StringToCoTaskMemUTF8(text);
				allocated.Add(pointer);
				return pointer;
			}
			case NativeKind.Ptr:
				if (value.IsNilOrNone)
					return (nint)0;
				if (!value.IsInteger)
					throw new KernPanic("ffi: аргумент ptr должен быть целым числом или nil");
				return (nint)value.AsI64;
			case NativeKind.Bool:
				return value.Kind == ValueKind.Bool
					? (byte)(value.AsBool ? 1 : 0)
					: (byte)(value.IsNilOrNone ? 0 : value.AsI64 != 0 ? 1 : 0);
			default:
				object? raw = value.IsNilOrNone ? 0 : KMarshal.ToObject(value);
				try
				{
					return Convert.ChangeType(raw ?? 0, NativeSignature.Clr(kind), invariant)!;
				}
				catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
				{
					throw new KernPanic(
						$"ffi: аргумент '{raw}' нельзя привести к {kind.ToString().ToLowerInvariant()}: {exception.Message}");
				}
		}
	}

	private static KValue FromNative(object? raw, NativeKind kind) => kind switch
	{
		NativeKind.Void => KValue.Nil,
		NativeKind.Bool => KValue.Bool((byte)raw! != 0),
		NativeKind.I8 => KValue.I8((sbyte)raw!),
		NativeKind.I16 => KValue.I16((short)raw!),
		NativeKind.I32 => KValue.I32((int)raw!),
		NativeKind.I64 => KValue.I64((long)raw!),
		NativeKind.U8 => KValue.U8((byte)raw!),
		NativeKind.U16 => KValue.U16((ushort)raw!),
		NativeKind.U32 => KValue.U32((uint)raw!),
		NativeKind.U64 => KValue.U64((ulong)raw!),
		NativeKind.F32 => KValue.F32((float)raw!),
		NativeKind.F64 => KValue.F64((double)raw!),
		NativeKind.Ptr => KValue.I64(unchecked((long)(nint)raw!)),
		_ => raw is nint pointer && pointer != 0
			? KValue.Str(Marshal.PtrToStringUTF8(pointer) ?? "")
			: KValue.Nil,
	};

	private static Caller Build(NativeSignature signature)
	{
		Type[] parameters = [.. signature.Params.Select(NativeSignature.Clr)];
		Type result = NativeSignature.Clr(signature.Return);
		var method = new DynamicMethod(
			"ffi_call",
			typeof(object),
			[typeof(nint), typeof(object[])],
			typeof(KernNativeFn).Module,
			skipVisibility: true);
		ILGenerator il = method.GetILGenerator();
		for (var index = 0; index < parameters.Length; index++)
		{
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Ldc_I4, index);
			il.Emit(OpCodes.Ldelem_Ref);
			il.Emit(OpCodes.Unbox_Any, parameters[index]);
		}

		il.Emit(OpCodes.Ldarg_0);
		il.EmitCalli(OpCodes.Calli, CallingConvention.Cdecl, result, parameters);
		if (result == typeof(void))
			il.Emit(OpCodes.Ldnull);
		else
			il.Emit(OpCodes.Box, result);
		il.Emit(OpCodes.Ret);
		return (Caller)method.CreateDelegate(typeof(Caller));
	}

	internal static KernNativeFn Require(KNativeBox self) => self.Value switch
	{
		KernNativeFn function => function,
		_ => throw new KernPanic($"native_fn: ожидалась функция, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Члены типа <c>native_fn</c>.</summary>
[KernType("native_fn")]
public static class NativeFnApi
{
	[KernProperty]
	public static string Name(KNativeBox self) => KernNativeFn.Require(self).Name;

	[KernProperty]
	public static string Signature(KNativeBox self) => KernNativeFn.Require(self).Signature;

	/// <summary><c>f.call([args])</c>: аргументы — <c>list</c>; результат — значение Kern.</summary>
	[KernMember]
	public static KValue Call(KNativeBox self, KValue arguments) =>
		KernNativeFn.Require(self).Call(arguments);
}
