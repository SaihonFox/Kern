using Kern.Bytecode;

namespace Kern.Vm.Types;

public enum KTypeKind
{
	Prim,
	Class,
	Builtin,
	Func,
	Tuple,
	Nullable,
	Union,
}

/// <summary>
/// Reified-тип времени выполнения. Экземпляры создаёт и интернирует <see cref="TypeFactory"/>,
/// поэтому сравнение типов — ссылочное (<c>==</c>), а не структурное.
/// </summary>
public abstract class KType
{
	public abstract KTypeKind Kind { get; }

	/// <summary>Плоский канонический текст (ключ интернирования и диагностика).</summary>
	public abstract string Canonical { get; }

	public sealed override string ToString() => Canonical;
}

/// <summary>Базовый тип (i32, str, bool, void, …).</summary>
public sealed class KPrimType : KType
{
	public PrimKind Prim { get; }

	internal KPrimType(PrimKind prim) => Prim = prim;

	public override KTypeKind Kind => KTypeKind.Prim;

	public override string Canonical => Prim.ToKernName();
}

/// <summary>Объявленный в модуле тип (type/unit/unit!) с reified-аргументами.</summary>
public sealed class KClassType : KType
{
	public TypeDef Definition { get; }
	public KType[] TypeArgs { get; }

	internal KClassType(TypeDef definition, KType[] typeArgs)
	{
		Definition = definition;
		TypeArgs = typeArgs;
	}

	public override KTypeKind Kind => KTypeKind.Class;

	public override string Canonical => TypeArgs.Length == 0
		? Definition.FullName
		: $"{Definition.FullName}<{KTypeText.Join(TypeArgs)}>";
}

/// <summary>Встроенный generic-тип рантайма: list/array/dict/result/task.</summary>
public sealed class KBuiltinGenericType : KType
{
	public BuiltinGenericKind Builtin { get; }
	public KType[] TypeArgs { get; }

	internal KBuiltinGenericType(BuiltinGenericKind builtin, KType[] typeArgs)
	{
		Builtin = builtin;
		TypeArgs = typeArgs;
	}

	public override KTypeKind Kind => KTypeKind.Builtin;

	public override string Canonical => $"{KTypeText.BuiltinName(Builtin)}<{KTypeText.Join(TypeArgs)}>";
}

/// <summary>Функциональный тип: параметры + результат.</summary>
public sealed class KFuncType : KType
{
	public KType Return { get; }
	public KType[] Params { get; }

	internal KFuncType(KType returnType, KType[] parameters)
	{
		Return = returnType;
		Params = parameters;
	}

	public override KTypeKind Kind => KTypeKind.Func;

	public override string Canonical => $"fun({KTypeText.Join(Params)}) -> {Return.Canonical}";
}

/// <summary>Кортеж; имена элементов — метаданные и в канонический текст не входят.</summary>
public sealed class KTupleType : KType
{
	public KType[] Items { get; }
	public string?[]? Names { get; }

	internal KTupleType(KType[] items, string?[]? names)
	{
		Items = items;
		Names = names;
	}

	public override KTypeKind Kind => KTypeKind.Tuple;

	public override string Canonical => $"({KTypeText.Join(Items)})";
}

/// <summary><c>T?</c></summary>
public sealed class KNullableType : KType
{
	public KType Inner { get; }

	internal KNullableType(KType inner) => Inner = inner;

	public override KTypeKind Kind => KTypeKind.Nullable;

	public override string Canonical => $"{Inner.Canonical}?";
}

/// <summary>Union из unit-типов.</summary>
public sealed class KUnionType : KType
{
	public KType[] Members { get; }

	internal KUnionType(KType[] members) => Members = members;

	public override KTypeKind Kind => KTypeKind.Union;

	public override string Canonical => $"[{KTypeText.Join(Members)}]";
}

/// <summary>
/// Встроенные generic-типы, которые не объявляются в TypePool модуля. Набор обязан совпадать
/// с <c>Kern.Compiler.Semantics.Types.BuiltinGenericKind</c>: компилятор пишет в подписи
/// канонические имена (<c>list</c>, <c>set</c>, <c>iter</c> …), и VM должна их разрешать.
/// </summary>
public enum BuiltinGenericKind
{
	List,
	Array,
	Dict,
	Set,
	Result,
	Task,
	Event,
	Observable,
	Channel,
	Pipe,
	Shared,
	Iter,
	AsyncIter,
	Collection,
	Queue,
	Stack,
}

internal static class KTypeText
{
	public static string Join(KType[] types) => string.Join(", ", types.Select(t => t.Canonical));

	public static string BuiltinName(BuiltinGenericKind kind) => kind switch
	{
		BuiltinGenericKind.List => "list",
		BuiltinGenericKind.Array => "array",
		BuiltinGenericKind.Dict => "dict",
		BuiltinGenericKind.Set => "set",
		BuiltinGenericKind.Result => "result",
		BuiltinGenericKind.Task => "task",
		BuiltinGenericKind.Event => "event",
		BuiltinGenericKind.Observable => "observable",
		BuiltinGenericKind.Channel => "channel",
		BuiltinGenericKind.Pipe => "pipe",
		BuiltinGenericKind.Shared => "shared",
		BuiltinGenericKind.Iter => "iter",
		BuiltinGenericKind.AsyncIter => "async_iter",
		BuiltinGenericKind.Collection => "collection",
		BuiltinGenericKind.Queue => "queue",
		BuiltinGenericKind.Stack => "stack",
		_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный встроенный generic"),
	};
}
