namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Разрешённый встроенный generic. Канонический текст — <c>dict</c> даже для устаревшего
/// написания <c>map</c>. <see cref="KernType.Kind"/> всегда <see cref="TypeKind.BuiltinGeneric"/>;
/// конкретный встроенный вид хранится в <see cref="BuiltinKind"/>.
/// </summary>
public sealed record BuiltinGenericType : KernType
{
	public BuiltinGenericType(BuiltinGenericKind builtinKind, IReadOnlyList<KernType> arguments)
		: base(TypeKind.BuiltinGeneric)
	{
		BuiltinKind = builtinKind;
		Arguments = StructuralList<KernType>.From(arguments);
	}

	public BuiltinGenericKind BuiltinKind { get; }

	public IReadOnlyList<KernType> Arguments { get; }

	public static int GetArity(BuiltinGenericKind kind) => kind switch
	{
		BuiltinGenericKind.Dict or BuiltinGenericKind.Result => 2,
		_ => 1
	};

	public static string GetCanonicalName(BuiltinGenericKind kind) => kind switch
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
		_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный встроенный generic")
	};

	public override string ToString() => TypeText.WithArguments(GetCanonicalName(BuiltinKind), Arguments);
}
