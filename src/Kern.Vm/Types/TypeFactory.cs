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

	private KType ResolveNamed(TypeSig.NamedSig named)
	{
		if (BuiltinKindOf(named.FullName) is { } builtin)
		{
			int expected = builtin switch
			{
				BuiltinGenericKind.List or BuiltinGenericKind.Array or BuiltinGenericKind.Task => 1,
				_ => 2,
			};
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

	private static BuiltinGenericKind? BuiltinKindOf(string name) => name switch
	{
		"list" => BuiltinGenericKind.List,
		"array" => BuiltinGenericKind.Array,
		"dict" => BuiltinGenericKind.Dict,
		"result" => BuiltinGenericKind.Result,
		"task" => BuiltinGenericKind.Task,
		_ => null,
	};

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
