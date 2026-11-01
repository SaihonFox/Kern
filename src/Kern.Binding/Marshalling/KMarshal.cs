using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Kern.Binding.Mapping;
using Kern.Binding.Values;
using Kern.Bytecode;
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
				return ToKResult(value, target);
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

		// Handle-тип хранится в KNativeBox: нативный член принимает сам CLR-объект
		// (`KernDuration`, `KernDateTime`), а не обёртку (spec §12.4).
		if (value.Ref is KNativeBox nativeBox && target.IsInstanceOfType(nativeBox.Value))
			return nativeBox.Value;

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

	/// <summary>
	/// Нативный <c>KResult&lt;T,E&gt;</c> → Kern-значение <c>Ok&lt;T&gt;</c>/<c>Err&lt;E&gt;</c>
	/// (spec §16): объект встроенного unit-типа модуля с единственным полем
	/// <c>value</c>/<c>error</c>. Reified-тип берётся из пула модуля интерпретатора,
	/// поэтому <c>match Ok/Err</c> в Kern видит тот же тип, что выдал компилятор.
	/// </summary>
	public static KValue FromKResult<T, E>(KResult<T, E> result, Interpreter interpreter)
	{
		ArgumentNullException.ThrowIfNull(interpreter);
		string variant = result.IsOk ? "Ok" : "Err";
		TypeDef definition = FindResultVariant(interpreter.Module, variant);
		KType argument = KernTypeOf(interpreter, result.IsOk ? typeof(T) : typeof(E));
		KClassType objectType = interpreter.Types.Named(definition, argument);
		KValue payload = result.IsOk ? FromObject(result.Ok) : FromObject(result.Err);

		KValue[] fields = new KValue[definition.Fields.Count];
		Array.Fill(fields, KValue.None);
		if (fields.Length > 0)
			fields[0] = payload;
		return KValue.Obj(new KObject(objectType, fields));
	}

	/// <summary>
	/// Встроенный вариант result в пуле модуля: полное имя <c>{модуль}.Ok</c>/<c>{модуль}.Err</c>
	/// с одним generic-параметром. Ищем по короткому имени — имя модуля в пуле одно.
	/// </summary>
	private static TypeDef FindResultVariant(KbcModule module, string variant)
	{
		TypeDef? candidate = null;
		foreach (TypeDef type in module.TypePool)
		{
			if (type.Name != variant)
				continue;
			if (type.GenericParams.Count == 1 && type.Fields.Count == 1)
				return type;
			candidate ??= type;
		}

		return candidate ?? throw new KernPanic(
			$"встроенный тип '{variant}' не найден в модуле '{module.Name}'");
	}

	/// <summary>Kern-значение <c>Ok</c>/<c>Err</c> → CLR <c>KResult&lt;T,E&gt;</c> (spec §16).</summary>
	private static object ToKResult(KValue value, Type target)
	{
		Type[] arguments = target.GetGenericArguments();
		if (value.Ref is not KObject instance || instance.FieldCount == 0)
			throw new KernPanic($"ожидался result, получено {value.TypeName}");

		bool isOk = instance.Type.Definition.Name == "Ok";
		if (!isOk && instance.Type.Definition.Name != "Err")
			throw new KernPanic($"ожидался result, получено {instance.Type.Canonical}");

		object? payload = To(instance.GetField(0), isOk ? arguments[0] : arguments[1]);
		MethodInfo factory = target.GetMethod(
			isOk ? "Success" : "Failure",
			BindingFlags.Public | BindingFlags.Static)
			?? throw new KernPanic($"KResult: у '{target.Name}' нет фабрики {(isOk ? "Success" : "Failure")}");
		return factory.Invoke(null, [payload])!;
	}

	/// <summary>
	/// CLR-тип значения → reified-тип VM. Нужен для аргумента <c>Ok&lt;T&gt;</c>/<c>Err&lt;E&gt;</c>:
	/// компилятор кодирует его той же подписью, поэтому соответствие обязано совпасть.
	/// </summary>
	private static KType KernTypeOf(Interpreter interpreter, Type type)
	{
		ArgumentNullException.ThrowIfNull(type);
		TypeFactory types = interpreter.Types;
		Type plain = Nullable.GetUnderlyingType(type) ?? type;
		if (plain != type)
			return types.Nullable(KernTypeOf(interpreter, plain));

		if (plain == typeof(bool)) return types.Prim(PrimKind.Bool);
		if (plain == typeof(char)) return types.Prim(PrimKind.Char);
		if (plain == typeof(sbyte)) return types.Prim(PrimKind.I8);
		if (plain == typeof(short)) return types.Prim(PrimKind.I16);
		if (plain == typeof(int)) return types.Prim(PrimKind.I32);
		if (plain == typeof(long)) return types.Prim(PrimKind.I64);
		if (plain == typeof(byte)) return types.Prim(PrimKind.U8);
		if (plain == typeof(ushort)) return types.Prim(PrimKind.U16);
		if (plain == typeof(uint)) return types.Prim(PrimKind.U32);
		if (plain == typeof(ulong)) return types.Prim(PrimKind.U64);
		if (plain == typeof(Half)) return types.Prim(PrimKind.F16);
		if (plain == typeof(float)) return types.Prim(PrimKind.F32);
		if (plain == typeof(double)) return types.Prim(PrimKind.F64);
		if (plain == typeof(string)) return types.Prim(PrimKind.Str);
		if (plain == typeof(void)) return types.Prim(PrimKind.Void);
		if (plain == typeof(KValue) || plain == typeof(object) || plain == typeof(KObject))
			return types.Prim(PrimKind.Object);

		if (plain.IsArray)
			return types.Builtin(BuiltinGenericKind.Array, KernTypeOf(interpreter, plain.GetElementType()!));

		if (plain.IsGenericType)
		{
			Type definition = plain.GetGenericTypeDefinition();
			Type[] arguments = plain.GetGenericArguments();
			if (definition == typeof(KList<>))
				return types.Builtin(BuiltinGenericKind.List, KernTypeOf(interpreter, arguments[0]));
			if (definition == typeof(KArray<>))
				return types.Builtin(BuiltinGenericKind.Array, KernTypeOf(interpreter, arguments[0]));
			if (definition == typeof(KDict<,>))
				return types.Builtin(
					BuiltinGenericKind.Dict,
					KernTypeOf(interpreter, arguments[0]),
					KernTypeOf(interpreter, arguments[1]));
			if (definition == typeof(KResult<,>))
				return types.Builtin(
					BuiltinGenericKind.Result,
					KernTypeOf(interpreter, arguments[0]),
					KernTypeOf(interpreter, arguments[1]));
		}

		// Нативный handle-тип (`duration`, `regex`): TypeDef есть в пуле модуля по Kern-имени.
		if (TypeMapping.NativeAttribute(plain) is { } attribute)
		{
			foreach (TypeDef candidate in interpreter.Module.TypePool)
				if (candidate.Name == attribute.Name && candidate.GenericParams.Count == 0)
					return types.Named(candidate);
		}

		return types.Prim(PrimKind.Object);
	}

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
