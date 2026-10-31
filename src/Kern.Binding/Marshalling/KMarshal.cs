using System.Collections;
using System.Runtime.CompilerServices;
using Kern.Binding.Values;
using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Binding.Marshalling;

/// <summary>
/// Маршаллинг Kern-значений в CLR и обратно по правилам spec §12.4. Все ошибки
/// несоответствия типов — <see cref="KernPanic"/> с русским сообщением.
/// </summary>
public static class KMarshal
{
	/// <summary>Kern-значение → CLR-значение типа <typeparamref name="T"/>.</summary>
	public static T To<T>(KValue value)
	{
		object? converted = To(value, typeof(T));
		return converted is null ? default! : (T)converted;
	}

	public static object? To(KValue value, Type target)
	{
		ArgumentNullException.ThrowIfNull(target);

		if (target == typeof(KValue))
			return value;
		if (target == typeof(object))
			return ToObject(value);

		Type nullable = Nullable.GetUnderlyingType(target) ?? target;
		if (nullable != target)
		{
			if (value.IsNilOrNone)
				return null;
			object? inner = To(value, nullable);
			return inner is null ? null : Activator.CreateInstance(target, inner);
		}

		if (target == typeof(string))
		{
			if (value.IsNilOrNone)
				return null;
			if (value.Kind != ValueKind.Str)
				throw new KernPanic($"ожидалось str, получено {value.TypeName}");
			return value.AsStr;
		}

		if (target == typeof(bool))
			return value.Kind == ValueKind.Bool
				? value.AsBool
				: throw new KernPanic($"ожидалось bool, получено {value.TypeName}");
		if (target == typeof(char))
			return value.Kind == ValueKind.Char
				? value.AsChar
				: throw new KernPanic($"ожидалось char, получено {value.TypeName}");
		if (target == typeof(sbyte))
			return (sbyte)RequireNumber(value, ValueKind.I8);
		if (target == typeof(short))
			return (short)RequireNumber(value, ValueKind.I16);
		if (target == typeof(int))
			return (int)RequireNumber(value, ValueKind.I32);
		if (target == typeof(long))
			return RequireNumber(value, ValueKind.I64);
		if (target == typeof(byte))
			return (byte)RequireNumber(value, ValueKind.U8);
		if (target == typeof(ushort))
			return (ushort)RequireNumber(value, ValueKind.U16);
		if (target == typeof(uint))
			return (uint)RequireNumber(value, ValueKind.U32);
		if (target == typeof(ulong))
			return (ulong)RequireNumber(value, ValueKind.U64);
		if (target == typeof(Half))
			return RequireFloat(value, ValueKind.F16);
		if (target == typeof(float))
			return (float)RequireFloat(value, ValueKind.F32);
		if (target == typeof(double))
			return RequireFloat(value, ValueKind.F64);

		if (target.IsEnum)
		{
			object numeric = To(value, Enum.GetUnderlyingType(target))!;
			return Enum.ToObject(target, numeric);
		}

		if (target.IsArray)
		{
			Type elementTarget = target.GetElementType()!;
			IReadOnlyList<KValue> items = value.Ref switch
			{
				KArray kernArray => kernArray.Items.ToArray(),
				KList list => list.Items,
				_ => throw new KernPanic($"ожидался массив, получено {value.TypeName}"),
			};
			Array result = Array.CreateInstance(elementTarget, items.Count);
			for (var index = 0; index < items.Count; index++)
				result.SetValue(To(items[index], elementTarget), index);
			return result;
		}

		if (target.IsGenericType)
		{
			Type definition = target.GetGenericTypeDefinition();
			if (definition == typeof(KList<>))
				return Activator.CreateInstance(target, value);
			if (definition == typeof(KArray<>))
				return Activator.CreateInstance(target, value);
			if (definition == typeof(KDict<,>))
				return Activator.CreateInstance(target, value);
			if (definition == typeof(KFunc<>))
				return Activator.CreateInstance(target, value);
			if (definition == typeof(KFunc<,>))
				return Activator.CreateInstance(target, value);
			if (definition == typeof(KAction<>))
				return Activator.CreateInstance(target, value);
			if (definition == typeof(KTask<>))
				return Activator.CreateInstance(target, value);
			if (definition == typeof(KResult<,>))
				throw new KernPanic("KResult: преобразование из VM-значения ещё не поддержано");
		}

		if (target == typeof(KAction))
			return new KAction(value);

		if (target == typeof(KTask))
			return value.Ref as KTask
				?? throw new KernPanic($"ожидалась задача, получено {value.TypeName}");
		if (target == typeof(KType))
			return value.Ref as KType
				?? throw new KernPanic($"ожидался тип, получено {value.TypeName}");
		if (target == typeof(KNativeBox))
			return value.Ref as KNativeBox
				?? throw new KernPanic($"ожидался нативный объект, получено {value.TypeName}");

		if (value.Ref is not null && target.IsInstanceOfType(value.Ref))
			return value.Ref;

		throw new KernPanic($"нельзя преобразовать {value.TypeName} в CLR-тип {target.Name}");
	}

	/// <summary>CLR-значение → Kern-значение.</summary>
	public static KValue From<T>(T value) => FromObject(value);

	public static KValue FromObject(object? value)
	{
		if (value is null)
			return KValue.Nil;
		if (value is KValue kern)
			return kern;
		if (value is string text)
			return KValue.Str(text);
		if (value is bool boolean)
			return KValue.Bool(boolean);
		if (value is char character)
			return KValue.Char(character);
		if (value is sbyte i8)
			return KValue.I8(i8);
		if (value is short i16)
			return KValue.I16(i16);
		if (value is int i32)
			return KValue.I32(i32);
		if (value is long i64)
			return KValue.I64(i64);
		if (value is byte u8)
			return KValue.U8(u8);
		if (value is ushort u16)
			return KValue.U16(u16);
		if (value is uint u32)
			return KValue.U32(u32);
		if (value is ulong u64)
			return KValue.U64(u64);
		if (value is Half f16)
			return KValue.F16(f16);
		if (value is float f32)
			return KValue.F32(f32);
		if (value is double f64)
			return KValue.F64(f64);
		if (value is Enum)
			return FromObject(Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType())));
		if (value is KList or KArray or KDict or KTuple or KObject or KClosure or KTask or KType)
			return KValue.Obj(value);
		if (value is Kern.Bytecode.FunctionDef)
			return KValue.Obj(value);
		if (value is KValue[] values)
			return KValue.Obj(new KArray(null, values));
		if (value is KNativeBox box)
			return KValue.Obj(box);
		if (value is KList<object> typedList)
			return typedList.Value;
		if (value is IEnumerable enumerable && value is not string)
		{
			var items = new List<KValue>();
			foreach (object? item in enumerable)
				items.Add(FromObject(item));
			return KValue.Obj(new KList(null, items));
		}

		return KValue.Obj(new KNativeBox(value));
	}

	/// <summary>Для `println`: значения показываются так, как их ожидает видеть пользователь.</summary>
	public static object? ToObject(KValue value) => value.Kind switch
	{
		ValueKind.None or ValueKind.Nil => null,
		ValueKind.Void => null,
		ValueKind.Bool => value.AsBool,
		ValueKind.Char => value.AsChar,
		ValueKind.Str => value.AsStr,
		ValueKind.I8 => value.AsI8,
		ValueKind.I16 => value.AsI16,
		ValueKind.I32 => value.AsI32,
		ValueKind.I64 => value.AsI64,
		ValueKind.U8 => value.AsU8,
		ValueKind.U16 => value.AsU16,
		ValueKind.U32 => value.AsU32,
		ValueKind.U64 => value.AsU64,
		ValueKind.F16 => value.AsF16,
		ValueKind.F32 => value.AsF32,
		ValueKind.F64 => value.AsF64,
		_ => value.Ref,
	};

	/// <summary>Обернуть .NET-задачу в Kern `task&lt;void&gt;`.</summary>
	public static KValue FromTask(Task task)
	{
		ArgumentNullException.ThrowIfNull(task);
		KTask result = new();
		task.ContinueWith(
			completed =>
			{
				if (completed.IsCanceled)
				{
					result.TrySetCanceled();
					return;
				}

				if (completed.IsFaulted)
				{
					Exception error = Unwrap(completed.Exception);
					result.TrySetError(new KernPanic(error.Message, error));
					return;
				}

				result.TrySetResult(KValue.Void);
			},
			TaskScheduler.Default);
		return KValue.Obj(result);
	}

	/// <summary>Обернуть .NET-задачу в Kern `task&lt;T&gt;`.</summary>
	public static KValue FromTask<T>(Task<T> task)
	{
		ArgumentNullException.ThrowIfNull(task);
		KTask result = new();
		task.ContinueWith(
			completed =>
			{
				if (completed.IsCanceled)
				{
					result.TrySetCanceled();
					return;
				}

				if (completed.IsFaulted)
				{
					Exception error = Unwrap(completed.Exception);
					result.TrySetError(new KernPanic(error.Message, error));
					return;
				}

				result.TrySetResult(FromObject(completed.Result));
			},
			TaskScheduler.Default);
		return KValue.Obj(result);
	}

	/// <summary>Уже готовая VM-задача → Kern-значение.</summary>
	public static KValue FromKTask(KTask task)
	{
		ArgumentNullException.ThrowIfNull(task);
		return KValue.Obj(task);
	}

	private static KValue FromTaskObject(object? task) => task switch
	{
		null => KValue.Nil,
		KTask kernTask => KValue.Obj(kernTask),
		Task plain => FromTask(plain),
		_ => throw new KernPanic($"неизвестный вид задачи: {task.GetType().Name}"),
	};

	/// <summary>Точка входа для resolver'а: возвращаемое значение native-метода → KValue.</summary>
	public static KValue FromReturn(object? value) =>
		value is Task task ? FromTask(task) : FromObject(value);

	private static Exception Unwrap(AggregateException? aggregate) =>
		aggregate?.InnerExceptions.Count == 1 ? aggregate.InnerExceptions[0] : aggregate ?? new Exception("задача завершилась ошибкой");

	private static long RequireNumber(KValue value, ValueKind expected)
	{
		if (value.Kind != expected)
			throw new KernPanic($"ожидалось {TypeName(expected)}, получено {value.TypeName}");
		return value.Bits;
	}

	private static double RequireFloat(KValue value, ValueKind expected)
	{
		if (value.Kind != expected)
			throw new KernPanic($"ожидалось {TypeName(expected)}, получено {value.TypeName}");
		return value.AsF64;
	}

	private static string TypeName(ValueKind kind) => kind switch
	{
		ValueKind.I8 => "i8",
		ValueKind.I16 => "i16",
		ValueKind.I32 => "i32",
		ValueKind.I64 => "i64",
		ValueKind.U8 => "u8",
		ValueKind.U16 => "u16",
		ValueKind.U32 => "u32",
		ValueKind.U64 => "u64",
		ValueKind.F16 => "f16",
		ValueKind.F32 => "f32",
		ValueKind.F64 => "f64",
		_ => kind.ToString(),
	};

	/// <summary>Хэш типа для кэшей маршаллинга.</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal static int TypeKey(Type type) => RuntimeHelpers.GetHashCode(type);
}
