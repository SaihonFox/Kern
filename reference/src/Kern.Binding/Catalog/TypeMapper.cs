using System.Numerics;
using System.Reflection;
using Kern.Binding.Attributes;
using Kern.Binding.Marshalling;
using Kern.Binding.Wrappers;
using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Binding.Catalog;

/// <summary>
/// Двунаправленное отображение C# Type ↔ TypeSig по разделу 6 ARCHITECTURE.md.
/// Имена встроенных контейнеров: list, array, dict, result, task. Остальные Named = «модуль.имя»
/// ([KernType]) либо «dotnet.Полное.Имя» (тип без атрибута — способ C).
/// </summary>
public sealed class TypeMapper(ModuleMapIndex modules)
{
	public const string ListName = "list", ArrayName = "array", DictName = "dict", ResultName = "result", TaskName = "task";

	private readonly Dictionary<Type, TypeSig> _custom = [];
	private readonly Dictionary<TypeSig, Type> _reverse = [];

	private static readonly Dictionary<Type, TypeSig> Prims = new()
	{
		[typeof(bool)] = new TypeSig.Prim(PrimKind.Bool),
		[typeof(char)] = new TypeSig.Prim(PrimKind.Char),
		[typeof(string)] = TypeSig.Str,
		[typeof(sbyte)] = new TypeSig.Prim(PrimKind.I8),
		[typeof(short)] = new TypeSig.Prim(PrimKind.I16),
		[typeof(int)] = TypeSig.I32,
		[typeof(long)] = TypeSig.I64,
		[typeof(byte)] = new TypeSig.Prim(PrimKind.U8),
		[typeof(ushort)] = new TypeSig.Prim(PrimKind.U16),
		[typeof(uint)] = new TypeSig.Prim(PrimKind.U32),
		[typeof(ulong)] = new TypeSig.Prim(PrimKind.U64),
		[typeof(Half)] = new TypeSig.Prim(PrimKind.F16),
		[typeof(float)] = new TypeSig.Prim(PrimKind.F32),
		[typeof(double)] = TypeSig.F64,
		[typeof(object)] = TypeSig.Object,
		[typeof(KValue)] = TypeSig.Object,
		[typeof(KObject)] = TypeSig.Object,
		[typeof(KType)] = new TypeSig.Prim(PrimKind.Type),
	};

	public ModuleMapIndex Modules { get; } = modules;

	/// <summary>Привязывает CLR-тип (self экстеншена: BigInteger, decimal…) к Kern-типу.</summary>
	public void RegisterSelf(Type clr, TypeSig sig) { _custom[clr] = sig; _reverse.TryAdd(sig, clr); }

	/// <summary>Нужно ли типу собственное Kern-имя: он не примитив, не сопоставлен ранее и не коллекция/Task.</summary>
	public bool NeedsRegistration(Type t) =>
		!Prims.ContainsKey(t) && !_custom.ContainsKey(t) && !t.IsGenericParameter && !t.IsArray
		&& !IsAsync(t) && !(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>));

	public static bool IsHidden(ParameterInfo p) =>
		p.GetCustomAttribute<KernVmAttribute>() is not null || p.GetCustomAttribute<KernTypeArgsAttribute>() is not null
		|| p.ParameterType == typeof(Interpreter);

	public static bool IsAsync(Type ret) =>
		ret == typeof(Task) || ret == typeof(ValueTask)
		|| (ret.IsGenericType && ret.GetGenericTypeDefinition() is var d && (d == typeof(Task<>) || d == typeof(ValueTask<>)));

	/// <summary>Возвращает T из Task&lt;T&gt;/ValueTask&lt;T&gt;, void для Task/ValueTask, иначе сам тип.</summary>
	public static Type Unwrap(Type ret) =>
		ret == typeof(Task) || ret == typeof(ValueTask) ? typeof(void)
		: IsAsync(ret) ? ret.GetGenericArguments()[0] : ret;

	/// <param name="member">Параметр/возврат/поле/свойство, к которому относится тип: нужен, чтобы отличить `T?` от голого `T` (рефлексия считает голый generic-T «возможно null»).</param>
	public TypeSig Map(Type t, NullabilityInfo? nullability = null, ICustomAttributeProvider? member = null)
	{
		if (t.IsGenericParameter && member is not null && !HasExplicitNullable(member)) return MapCore(t);
		var sig = MapCore(t);
		if (sig is not TypeSig.Nullable && !t.IsValueType && nullability is { } n
			&& (n.ReadState == NullabilityState.Nullable || n.WriteState == NullabilityState.Nullable))
			return new TypeSig.Nullable(sig);
		return sig;
	}

	/// <summary>Тип возврата метода; для Task/ValueTask → task&lt;T&gt;.</summary>
	public TypeSig MapReturn(MethodInfo m, NullabilityInfoContext ctx)
	{
		var ret = m.ReturnType;
		var nullability = ctx.Create(m.ReturnParameter);
		if (!IsAsync(ret)) return Map(ret, nullability, m.ReturnParameter);
		var inner = Unwrap(ret);
		var innerNullability = nullability.GenericTypeArguments.Length > 0 ? nullability.GenericTypeArguments[0] : null;
		return new TypeSig.Named(TaskName, [Map(inner, innerNullability, m.ReturnParameter)]);
	}

	/// <summary>Есть ли у члена атрибут компилятора [Nullable(2)] — то есть тип явно записан как `T?`.</summary>
	private static bool HasExplicitNullable(ICustomAttributeProvider member)
	{
		var data = member switch
		{
			ParameterInfo p => p.GetCustomAttributesData(),
			MemberInfo m => m.GetCustomAttributesData(),
			_ => [],
		};
		foreach (var a in data)
		{
			if (a.AttributeType.FullName != "System.Runtime.CompilerServices.NullableAttribute" || a.ConstructorArguments.Count == 0) continue;
			var arg = a.ConstructorArguments[0].Value;
			if (arg is byte b) return b == 2;
			if (arg is IReadOnlyCollection<CustomAttributeTypedArgument> list && list.Count > 0) return list.First().Value is byte f && f == 2;
		}
		return false;
	}

	private TypeSig MapCore(Type t)
	{
		if (t == typeof(void)) return TypeSig.Void;
		if (t.IsByRef) return MapCore(t.GetElementType()!);
		if (t.IsGenericParameter) return new TypeSig.Param(t.DeclaringMethod is not null ? 1 : 0, t.GenericParameterPosition, t.Name);
		if (Nullable.GetUnderlyingType(t) is { } u) return new TypeSig.Nullable(MapCore(u));
		if (Prims.TryGetValue(t, out var p)) return p;
		if (_custom.TryGetValue(t, out var c)) return c;
		if (t.IsArray) return new TypeSig.Named(ArrayName, [MapCore(t.GetElementType()!)]);
		if (t == typeof(Task) || t == typeof(ValueTask)) return new TypeSig.Named(TaskName, [TypeSig.Void]);
		if (t == typeof(KKeyless)) return TypeSig.Object;

		if (t.IsGenericType)
		{
			var def = t.GetGenericTypeDefinition();
			var args = t.GetGenericArguments();
			TypeSig[] Mapped() => args.Select(MapCore).ToArray();
			if (def == typeof(KList<>)) return new TypeSig.Named(ListName, Mapped());
			if (def == typeof(KArray<>)) return new TypeSig.Named(ArrayName, Mapped());
			if (def == typeof(KDict<,>)) return new TypeSig.Named(DictName, Mapped());
			if (def == typeof(KResult<,>)) return new TypeSig.Named(ResultName, Mapped());
			if (def == typeof(Task<>) || def == typeof(ValueTask<>)) return new TypeSig.Named(TaskName, Mapped());
			if (def == typeof(KTaskRef<>)) return new TypeSig.Named(TaskName, Mapped());
			if (def == typeof(KTaskNullable<>)) return new TypeSig.Named(TaskName, [new TypeSig.Nullable(Mapped()[0])]);
			if (def.Name.StartsWith("KTuple`", StringComparison.Ordinal) && def.Namespace == typeof(KList<>).Namespace)
				return new TypeSig.Tuple(Mapped(), null);
			if (def.FullName!.StartsWith("System.ValueTuple`", StringComparison.Ordinal)) return new TypeSig.Tuple(Mapped(), null);
			if (def.Namespace == typeof(KList<>).Namespace && def.Name.StartsWith("KFunc`", StringComparison.Ordinal))
				return new TypeSig.Func(Mapped()[..^1], Mapped()[^1]);
			if (def.Namespace == typeof(KList<>).Namespace && def.Name.StartsWith("KAction`", StringComparison.Ordinal))
				return new TypeSig.Func(Mapped(), TypeSig.Void);
			if (IsListLike(def)) return new TypeSig.Named(ListName, [MapCore(args[0])]);
			if (IsDictLike(def)) return new TypeSig.Named(DictName, Mapped());
		}
		if (t.Namespace == "Kern.Vm.Async" && t.Name == "KTask") return new TypeSig.Named(TaskName, [TypeSig.Object]);
		if (t == typeof(KRange)) return new TypeSig.Named("kern.range", []);
		if (t == typeof(KFunc.Callable)) return TypeSig.Object;
		if (t.Namespace == typeof(KList<>).Namespace && t.Name == "KFunc") return TypeSig.Object;
		if (t.Namespace == typeof(KList<>).Namespace && t.Name == nameof(KAction)) return new TypeSig.Func([], TypeSig.Void);
		if (typeof(Delegate).IsAssignableFrom(t) && t.GetMethod("Invoke") is { } inv)
			return new TypeSig.Func(inv.GetParameters().Select(x => MapCore(x.ParameterType)).ToArray(), MapCore(inv.ReturnType));

		if (t.GetCustomAttribute<KernTypeAttribute>() is not null || (t.IsGenericType && t.GetGenericTypeDefinition().GetCustomAttribute<KernTypeAttribute>() is not null))
		{
			var def = t.IsGenericType ? t.GetGenericTypeDefinition() : t;
			var targs = t.IsGenericType ? t.GetGenericArguments().Select(MapCore).ToArray() : [];
			return KernNamed(def, targs);
		}
		{   // тип без атрибута (способ C): dotnet.Полное.Имя<args>
			var def = t.IsGenericType ? t.GetGenericTypeDefinition() : t;
			var targs = t.IsGenericType ? t.GetGenericArguments().Select(MapCore).ToArray() : [];
			return new TypeSig.Named(DotnetName(def), targs);
		}
	}

	private static bool IsListLike(Type def) =>
		def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IReadOnlyList<>) || def == typeof(ICollection<>)
		|| def == typeof(IReadOnlyCollection<>) || def == typeof(IEnumerable<>);

	private static bool IsDictLike(Type def) =>
		def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>);

	/// <summary>Имя Named для типа с [KernType]: «модуль.имя».</summary>
	public TypeSig.Named KernNamed(Type def, TypeSig[] args)
	{
		var kt = def.GetCustomAttribute<KernTypeAttribute>();
		var name = kt?.Name ?? SnakeCase.Of(StripArity(def.Name));
		return new TypeSig.Named($"{Modules.ModuleOf(def)}.{name}", args);
	}

	public static string DotnetName(Type def) => "dotnet." + StripArity((def.FullName ?? def.Name).Replace('+', '.'));

	private static string StripArity(string n) { int i = n.IndexOf('`'); return i < 0 ? n : n[..i]; }

	// ---- Обратное отображение: TypeSig → CLR-тип обёртки (generic-аргументы = KValue) ----

	public Type? ToClr(TypeSig sig)
	{
		switch (sig)
		{
			case TypeSig.Prim pr:
				foreach (var kv in Prims) if (kv.Value == pr && kv.Key != typeof(KValue) && kv.Key != typeof(KObject) && (pr.Kind != PrimKind.Object || kv.Key == typeof(object))) return kv.Key;
				return pr.Kind == PrimKind.Void ? typeof(void) : null;
			case TypeSig.Param or TypeSig.Union: return typeof(KValue);
			case TypeSig.Nullable n:
				var inner = ToClr(n.Inner);
				return inner is { IsValueType: true } ? typeof(Nullable<>).MakeGenericType(inner) : inner;
			case TypeSig.Func f:
				var k = typeof(KValue);
				var targs = f.Params.Select(_ => k).ToList();
				if (f.Return == TypeSig.Void) return ActionType(targs.Count)?.MakeGenericTypeOrSelf(targs);
				targs.Add(k);
				return FuncType(targs.Count)?.MakeGenericType([.. targs]);
			case TypeSig.Tuple t:
				return TupleType(t.Items.Count)?.MakeGenericType(t.Items.Select(_ => typeof(KValue)).ToArray());
			case TypeSig.Named nm:
				var a = nm.Args.Select(_ => typeof(KValue)).ToArray();
				return nm.FullName switch
				{
					ListName => typeof(KList<KValue>),
					ArrayName => typeof(KArray<KValue>),
					DictName => typeof(KDict<KValue, KValue>),
					ResultName => typeof(KResult<KValue, KValue>),
					_ => _reverse.GetValueOrDefault(sig) ?? _reverse.GetValueOrDefault(new TypeSig.Named(nm.FullName, [])),
				};
		}
		return null;
	}

	private static Type? FuncType(int n) => n switch { 1 => typeof(KFunc<>), 2 => typeof(KFunc<,>), 3 => typeof(KFunc<,,>), 4 => typeof(KFunc<,,,>), _ => null };
	private static Type? ActionType(int n) => n switch { 0 => typeof(KAction), 1 => typeof(KAction<>), 2 => typeof(KAction<,>), _ => null };
	private static Type? TupleType(int n) => n switch { 2 => typeof(KTuple<,>), 3 => typeof(KTuple<,,>), _ => null };
}

internal static class TypeExt
{
	public static Type MakeGenericTypeOrSelf(this Type t, List<Type> args) => t.IsGenericTypeDefinition ? t.MakeGenericType([.. args]) : t;
}

/// <summary>Служебный маркер (никогда не встречается в сигнатурах); оставлен для будущих расширений маппера.</summary>
internal sealed class KKeyless;
