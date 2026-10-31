using Kern.Bytecode;
using Kern.Vm.Values;

namespace Kern.Vm.Types;

/// <summary>Создаёт и интернирует KType: равные типы — одна и та же ссылка.</summary>
public sealed class TypeFactory(KModuleInfo module)
{
	private static readonly KPrimType[] PrimTable = Enum.GetValues<PrimKind>().Select(k => new KPrimType(k)).ToArray();
	private readonly Dictionary<string, KType> _interned = [];
	private readonly Dictionary<string, Type> _natives = [];
	private readonly object _lock = new();

	public KModuleInfo Module { get; } = module;

	public static KPrimType Prim(PrimKind k) => PrimTable[(int)k];
	public static KPrimType Object => Prim(PrimKind.Object);
	public static KPrimType Str => Prim(PrimKind.Str);
	public static KPrimType I32 => Prim(PrimKind.I32);
	public static KPrimType Void => Prim(PrimKind.Void);

	/// <summary>Привязать имя Kern-типа (Named вне модуля) к CLR-типу.</summary>
	public void RegisterNative(string kernName, Type clr) { lock (_lock) _natives[kernName] = clr; }

	public KType Resolve(TypeSig sig, KType[] classArgs, KType[] methodArgs)
	{
		switch (sig)
		{
			case TypeSig.Prim p: return Prim(p.Kind);
			case TypeSig.Param pa:
			{
				var arr = pa.Scope == 0 ? classArgs : methodArgs;
				return pa.Index >= 0 && pa.Index < arr.Length ? arr[pa.Index] : Object;
			}
			case TypeSig.Named n:
			{
				var args = new KType[n.Args.Count];
				for (int i = 0; i < args.Length; i++) args[i] = Resolve(n.Args[i], classArgs, methodArgs);
				return Named(n.FullName, args);
			}
			case TypeSig.Func f:
			{
				var ps = new KType[f.Params.Count];
				for (int i = 0; i < ps.Length; i++) ps[i] = Resolve(f.Params[i], classArgs, methodArgs);
				return Func(ps, Resolve(f.Return, classArgs, methodArgs));
			}
			case TypeSig.Tuple t:
			{
				var items = new KType[t.Items.Count];
				for (int i = 0; i < items.Length; i++) items[i] = Resolve(t.Items[i], classArgs, methodArgs);
				return Tuple(items, t.Names?.ToArray());
			}
			case TypeSig.Nullable nu: return Nullable(Resolve(nu.Inner, classArgs, methodArgs));
			case TypeSig.Union u:
			{
				var ms = new KType[u.Members.Count];
				for (int i = 0; i < ms.Length; i++) ms[i] = Resolve(u.Members[i], classArgs, methodArgs);
				return Union(ms);
			}
			default: throw new KernPanicTypes("неизвестная подпись типа " + sig);
		}
	}

	/// <summary>Есть ли в подписи параметры типа (тогда результат зависит от фрейма и не кэшируется глобально).</summary>
	public static bool HasParams(TypeSig sig) => sig switch
	{
		TypeSig.Param => true,
		TypeSig.Named n => n.Args.Any(HasParams),
		TypeSig.Func f => f.Params.Any(HasParams) || HasParams(f.Return),
		TypeSig.Tuple t => t.Items.Any(HasParams),
		TypeSig.Nullable nu => HasParams(nu.Inner),
		TypeSig.Union u => u.Members.Any(HasParams),
		_ => false,
	};

	private static string Key(KType[] a) => string.Join(",", a.Select(x => x.Id));

	private T Intern<T>(string key, Func<T> make) where T : KType
	{
		lock (_lock)
		{
			if (_interned.TryGetValue(key, out var t)) return (T)t;
			var created = make();
			_interned[key] = created;
			return created;
		}
	}

	public KType Named(string fullName, KType[] args)
	{
		if (Module.Index is { } idx && idx.TypeByName.TryGetValue(fullName, out int di))
			return Class(di, args);
		return Intern("N:" + fullName + "<" + Key(args) + ">", () =>
			_natives.TryGetValue(fullName, out var clr)
				? new KNativeType(clr, fullName, args)
				: (KType)new KBuiltinGenericType(fullName, args));
	}

	public KClassType Class(int defIndex, KType[] args) =>
		Intern("C:" + defIndex + "<" + Key(args) + ">", () => new KClassType(Module.Module.Types[defIndex], defIndex, args, Module));

	public KFuncType Func(KType[] ps, KType ret) =>
		Intern("F:" + Key(ps) + "->" + ret.Id, () => new KFuncType(ps, ret));

	public KTupleType Tuple(KType[] items, string?[]? names) =>
		Intern("T:" + Key(items), () => new KTupleType(items, names));

	public KType Nullable(KType inner) =>
		inner is KNullableType ? inner : Intern<KNullableType>("Q:" + inner.Id, () => new KNullableType(inner));

	public KUnionType Union(KType[] members) => Intern("U:" + Key(members), () => new KUnionType(members));

	public KType List(KType elem) => Named("list", [elem]);
	public KType Array(KType elem) => Named("array", [elem]);
	public KType Dict(KType k, KType v) => Named("dict", [k, v]);
	public KType Result(KType ok, KType err) => Named("result", [ok, err]);
	public KType Task(KType t) => Named("task", [t]);
}

/// <summary>Ошибка фабрики типов (внутренняя, не panic Kern).</summary>
public sealed class KernPanicTypes(string message) : Exception(message);
