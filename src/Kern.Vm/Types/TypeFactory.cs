using Kern.Bytecode;

namespace Kern.Vm.Types;

/// <summary>
/// Фабрика reified-типов одной VM: интернирует экземпляры, чтобы сравнение типов было
/// ссылочным. Не использует глобального состояния; кэш защищён блокировкой, так как
/// <c>Interpreter.Fork()</c> сможет делить типы между потоками (DECISIONS, раунд 24).
/// </summary>
public sealed class TypeFactory
{
	private readonly KbcModule _module;
	private readonly Dictionary<string, KType> _cache = new(StringComparer.Ordinal);
	private readonly Lock _gate = new();

	public TypeFactory(KbcModule module) => _module = module ?? throw new ArgumentNullException(nameof(module));

	public KbcModule Module => _module;

	public KPrimType Prim(PrimKind kind) =>
		Intern($"p:{kind.ToKernName()}", () => new KPrimType(kind));

	public KClassType Named(TypeDef definition, params KType[] typeArgs)
	{
		ArgumentNullException.ThrowIfNull(definition);
		KType[] args = [.. typeArgs];
		string key = args.Length == 0
			? $"c:{definition.FullName}"
			: $"c:{definition.FullName}<{KTypeText.Join(args)}>";
		return Intern(key, () => new KClassType(definition, args));
	}

	public KBuiltinGenericType Builtin(BuiltinGenericKind kind, params KType[] typeArgs)
	{
		KType[] args = [.. typeArgs];
		string key = $"b:{kind}<{KTypeText.Join(args)}>";
		return Intern(key, () => new KBuiltinGenericType(kind, args));
	}

	public KFuncType Func(KType returnType, params KType[] parameters)
	{
		ArgumentNullException.ThrowIfNull(returnType);
		KType[] ps = [.. parameters];
		string key = $"f:({KTypeText.Join(ps)})->{returnType.Canonical}";
		return Intern(key, () => new KFuncType(returnType, ps));
	}

	public KTupleType Tuple(KType[] items, string?[]? names = null)
	{
		ArgumentNullException.ThrowIfNull(items);
		KType[] copy = [.. items];
		string key = $"t:({KTypeText.Join(copy)})";
		return Intern(key, () => new KTupleType(copy, names));
	}

	public KNullableType Nullable(KType inner)
	{
		ArgumentNullException.ThrowIfNull(inner);
		return Intern($"n:{inner.Canonical}", () => new KNullableType(inner));
	}

	public KUnionType Union(params KType[] members)
	{
		KType[] copy = [.. members];
		return Intern($"u:({KTypeText.Join(copy)})", () => new KUnionType(copy));
	}

	/// <summary>Разрешить подпись модуля в reified-тип (параметры дженериков требуют подстановки).</summary>
	public KType FromSig(TypeSig sig)
	{
		ArgumentNullException.ThrowIfNull(sig);
		return sig switch
		{
			TypeSig.PrimSig prim => Prim(prim.Kind),
			TypeSig.NamedSig named => ResolveNamed(named),
			TypeSig.FuncSig func => Func(FromSig(func.Return), [.. func.Params.Select(FromSig)]),
			TypeSig.TupleSig tuple => Tuple([.. tuple.Items.Select(FromSig)], tuple.Names),
			TypeSig.NullableSig nullable => Nullable(FromSig(nullable.Inner)),
			TypeSig.UnionSig union => Union([.. union.Members.Select(FromSig)]),
			TypeSig.ParamSig param => throw new InvalidOperationException(
				$"параметр типа '{param.Canonical}' нельзя разрешить без подстановки дженерика"),
			_ => throw new InvalidOperationException($"неизвестная подпись типа: {sig.Canonical}"),
		};
	}

	/// <summary>
	/// Разрешить подпись родителя в контексте reified-типа: параметры типа (scope 0)
	/// подставляются аргументами <paramref name="owner"/>. Нужно для проверки
	/// «объект наследника является экземпляром родителя».
	/// </summary>
	public KType FromSig(TypeSig sig, KClassType owner)
	{
		ArgumentNullException.ThrowIfNull(sig);
		ArgumentNullException.ThrowIfNull(owner);
		var arguments = owner.TypeArgs;

		KType Walk(TypeSig current) => current switch
		{
			TypeSig.ParamSig { Scope: 0 } param when (uint)param.Index < (uint)arguments.Length =>
				arguments[param.Index],
			TypeSig.PrimSig prim => Prim(prim.Kind),
			TypeSig.NamedSig named => ResolveNamedParts(named.FullName, [.. named.Args.Select(Walk)]),
			TypeSig.FuncSig func => Func(Walk(func.Return), [.. func.Params.Select(Walk)]),
			TypeSig.TupleSig tuple => Tuple([.. tuple.Items.Select(Walk)], tuple.Names),
			TypeSig.NullableSig nullable => Nullable(Walk(nullable.Inner)),
			TypeSig.UnionSig union => Union([.. union.Members.Select(Walk)]),
			TypeSig.ParamSig param => throw new InvalidOperationException(
				$"параметр типа '{param.Canonical}' вне аргументов типа '{owner.Canonical}'"),
			_ => throw new InvalidOperationException($"неизвестная подпись типа: {current.Canonical}"),
		};

		return Walk(sig);
	}

	private KType ResolveNamedParts(string fullName, KType[] arguments)
	{
		if (BuiltinKindOf(fullName) is { } builtin)
		{
			int expected = ArityOf(builtin);
			if (arguments.Length != expected)
				throw new InvalidOperationException(
					$"встроенный тип '{fullName}' ожидает {expected} аргументов, передано {arguments.Length}");
			return Builtin(builtin, arguments);
		}

		TypeDef? definition = _module.FindType(fullName);
		if (definition is null)
			throw new InvalidOperationException($"тип '{fullName}' не найден в модуле '{_module.Name}'");
		if (definition.GenericParams.Count != arguments.Length)
			throw new InvalidOperationException(
				$"тип '{fullName}' ожидает {definition.GenericParams.Count} аргументов, передано {arguments.Length}");
		return Named(definition, arguments);
	}

	private KType ResolveNamed(TypeSig.NamedSig named)
	{
		if (BuiltinKindOf(named.FullName) is { } builtin)
		{
			int expected = ArityOf(builtin);
			if (named.Args.Length != expected)
				throw new InvalidOperationException(
					$"встроенный тип '{named.FullName}' ожидает {expected} аргументов, передано {named.Args.Length}");
			return Builtin(builtin, [.. named.Args.Select(FromSig)]);
		}

		TypeDef? definition = _module.FindType(named.FullName);
		if (definition is null)
			throw new InvalidOperationException($"тип '{named.FullName}' не найден в модуле '{_module.Name}'");
		if (definition.GenericParams.Count != named.Args.Length)
			throw new InvalidOperationException(
				$"тип '{named.FullName}' ожидает {definition.GenericParams.Count} аргументов, передано {named.Args.Length}");
		return Named(definition, [.. named.Args.Select(FromSig)]);
	}

	/// <summary>Канонические имена встроенных generic-типов (совпадают с компилятором).</summary>
	private static BuiltinGenericKind? BuiltinKindOf(string name) => name switch
	{
		"list" => BuiltinGenericKind.List,
		"array" => BuiltinGenericKind.Array,
		"dict" => BuiltinGenericKind.Dict,
		"set" => BuiltinGenericKind.Set,
		"result" => BuiltinGenericKind.Result,
		"task" => BuiltinGenericKind.Task,
		"event" => BuiltinGenericKind.Event,
		"observable" => BuiltinGenericKind.Observable,
		"channel" => BuiltinGenericKind.Channel,
		"pipe" => BuiltinGenericKind.Pipe,
		"shared" => BuiltinGenericKind.Shared,
		"iter" => BuiltinGenericKind.Iter,
		"async_iter" => BuiltinGenericKind.AsyncIter,
		"collection" => BuiltinGenericKind.Collection,
		"queue" => BuiltinGenericKind.Queue,
		"stack" => BuiltinGenericKind.Stack,
		_ => null,
	};

	/// <summary>Арность встроенного generic-типа: как у <c>BuiltinGenericType.GetArity</c> компилятора.</summary>
	private static int ArityOf(BuiltinGenericKind kind) =>
		kind is BuiltinGenericKind.Dict or BuiltinGenericKind.Result ? 2 : 1;

	private T Intern<T>(string key, Func<T> create) where T : KType
	{
		lock (_gate)
		{
			if (_cache.TryGetValue(key, out KType? existing)) return (T)existing;
			T created = create();
			_cache[key] = created;
			return created;
		}
	}
}
