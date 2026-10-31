using System.Runtime.CompilerServices;
using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Reflection;

/// <summary>Видимость члена (по MemberFlags; без явного модификатора: тип — Public, член — Private, как в спецификации Kern).</summary>
public enum MemberAccess { Public, Private, Protected, Internal }

/// <summary>Точка входа рефлексии: получение TypeInfo/MethodInfo по значению, имени, атрибуту. Кэшируется на уровне VM.</summary>
public static class Reflector
{
	private sealed class Cache
	{
		public readonly Dictionary<KType, TypeInfo> Types = [];
		public readonly Dictionary<(int Function, int Declaring), MethodInfo> Methods = [];
		public readonly object Lock = new();
	}

	private static readonly ConditionalWeakTable<Interpreter, Cache> Caches = new();

	private static Cache CacheOf(Interpreter vm) => Caches.GetValue(vm, _ => new Cache());

	// ---------- типы ----------

	public static TypeInfo TypeOf(Interpreter vm, KType type)
	{
		var c = CacheOf(vm);
		lock (c.Lock)
		{
			if (!c.Types.TryGetValue(type, out var ti)) c.Types[type] = ti = new TypeInfo(vm, type);
			return ti;
		}
	}

	/// <summary>Динамический тип значения.</summary>
	public static TypeInfo TypeOf(Interpreter vm, KValue value) => TypeOf(vm, vm.TypeOf(value));

	/// <summary>Тип модуля по индексу TypeDef; недостающие аргументы типа добавляются как object.</summary>
	public static TypeInfo ClassOf(Interpreter vm, int defIndex, KType[]? args = null) =>
		TypeOf(vm, vm.Types.Class(defIndex, PadArgs(vm, defIndex, args)));

	internal static KType[] PadArgs(Interpreter vm, int defIndex, KType[]? args)
	{
		int need = vm.Module.Types[defIndex].GenericParams.Count;
		args ??= [];
		if (args.Length == need) return args;
		var r = new KType[need];
		for (int i = 0; i < need; i++) r[i] = i < args.Length ? args[i] : TypeFactory.Object;
		return r;
	}

	/// <summary>Индекс типа модуля по полному либо короткому имени (также с суффиксом Attribute); -1 если нет.</summary>
	public static int FindTypeIndex(Interpreter vm, string name)
	{
		if (vm.Info.Index.TypeByName.TryGetValue(name, out int i)) return i;
		var types = vm.Module.Types;
		for (int k = 0; k < types.Count; k++)
			if (types[k].Name == name) return k;
		for (int k = 0; k < types.Count; k++)
			if (types[k].Name == name + "Attribute" || types[k].FullName.EndsWith("." + name, StringComparison.Ordinal)) return k;
		return -1;
	}

	/// <summary>Найти тип модуля по имени (конструируя generic с указанными аргументами, если они заданы).</summary>
	public static TypeInfo? FindType(Interpreter vm, string name, params KType[] typeArgs)
	{
		int i = FindTypeIndex(vm, name);
		return i < 0 ? null : ClassOf(vm, i, typeArgs);
	}

	/// <summary>Все типы, объявленные в модуле (generic — с аргументами object).</summary>
	public static IReadOnlyList<TypeInfo> DeclaredTypes(Interpreter vm)
	{
		var r = new TypeInfo[vm.Module.Types.Count];
		for (int i = 0; i < r.Length; i++) r[i] = ClassOf(vm, i);
		return r;
	}

	public static IReadOnlyList<TypeInfo> TypesWithAttribute(Interpreter vm, string attribute) =>
		DeclaredTypes(vm).Where(t => t.HasAttribute(attribute)).ToArray();

	// ---------- функции ----------

	public static MethodInfo MethodOf(Interpreter vm, int functionIndex, TypeInfo? declaring = null)
	{
		var fd = vm.GetFunction(functionIndex);
		if (declaring is null && fd.Owner >= 0) declaring = ClassOf(vm, fd.Owner);
		var c = CacheOf(vm);
		var key = (functionIndex, declaring?.Type.Id ?? 0);
		lock (c.Lock)
		{
			if (!c.Methods.TryGetValue(key, out var m)) c.Methods[key] = m = new MethodInfo(vm, functionIndex, declaring);
			return m;
		}
	}

	/// <summary>Верхнеуровневые функции модуля (без владельца, не лямбды).</summary>
	public static IReadOnlyList<MethodInfo> Functions(Interpreter vm)
	{
		var r = new List<MethodInfo>();
		for (int i = 0; i < vm.Module.Functions.Count; i++)
		{
			var f = vm.Module.Functions[i];
			if (f.Owner < 0 && !f.Flags.HasFlag(MemberFlags.Lambda)) r.Add(MethodOf(vm, i));
		}
		return r;
	}

	public static MethodInfo? FindFunction(Interpreter vm, string name) => Functions(vm).FirstOrDefault(f => f.Name == name);

	/// <summary>Все функции модуля (включая члены типов), помеченные атрибутом.</summary>
	public static IReadOnlyList<MethodInfo> FunctionsWithAttribute(Interpreter vm, string attribute)
	{
		var r = new List<MethodInfo>();
		for (int i = 0; i < vm.Module.Functions.Count; i++)
		{
			if (vm.Module.Functions[i].Attributes.Any(a => NameMatches(a.TypeName, attribute))) r.Add(MethodOf(vm, i));
		}
		return r;
	}

	/// <summary>Тестовые функции (KbcModule.Tests).</summary>
	public static IReadOnlyList<MethodInfo> Tests(Interpreter vm) => vm.Module.Tests.Select(i => MethodOf(vm, i)).ToArray();

	public static IReadOnlyList<AttributeInfo> ModuleAttributes(Interpreter vm) => AttributeInfo.Wrap(vm, vm.Module.Attributes);

	// ---------- общие помощники ----------

	/// <summary>Совпадение имени атрибута: точное, по хвосту после точки, с/без суффикса Attribute.</summary>
	public static bool NameMatches(string have, string want)
	{
		static string Trim(string s) => s.EndsWith("Attribute", StringComparison.Ordinal) && s.Length > 9 ? s[..^9] : s;
		have = Trim(have);
		want = Trim(want);
		return have == want || have.EndsWith("." + want, StringComparison.Ordinal);
	}

	internal static MemberAccess AccessOf(MemberFlags f, MemberAccess @default) =>
		f.HasFlag(MemberFlags.Pub) ? MemberAccess.Public :
		f.HasFlag(MemberFlags.Priv) ? MemberAccess.Private :
		f.HasFlag(MemberFlags.Prot) ? MemberAccess.Protected :
		f.HasFlag(MemberFlags.Int) ? MemberAccess.Internal : @default;

	/// <summary>Константа атрибута/параметра → значение VM (с приведением числа к ожидаемому примитиву).</summary>
	internal static KValue ConstantToValue(Interpreter vm, ConstantArg c, KType? expected = null)
	{
		KValue v = c.Value switch
		{
			null => KValue.Nil,
			TypeSig ts => KValue.Obj(new KTypeObject(vm.Types.Resolve(ts, [], []))),
			var o => Num.FromClr(o),
		};
		return Coerce(v, expected);
	}

	internal static KValue Coerce(KValue v, KType? expected)
	{
		if (expected is KPrimType p && v.IsNumber && NumericKind(p.Kind) is { } vk && vk != v.Kind)
			return Num.Convert(v, vk);
		return v;
	}

	internal static ValueKind? NumericKind(PrimKind k) => k switch
	{
		PrimKind.I8 => ValueKind.I8, PrimKind.I16 => ValueKind.I16, PrimKind.I32 => ValueKind.I32, PrimKind.I64 => ValueKind.I64,
		PrimKind.U8 => ValueKind.U8, PrimKind.U16 => ValueKind.U16, PrimKind.U32 => ValueKind.U32, PrimKind.U64 => ValueKind.U64,
		PrimKind.F16 => ValueKind.F16, PrimKind.F32 => ValueKind.F32, PrimKind.F64 => ValueKind.F64,
		_ => null,
	};

	/// <summary>Индекс статического поля в KbcModule.Globals / Info.Statics.</summary>
	internal static int StaticIndex(Interpreter vm, TypeDef owner, FieldDef f)
	{
		var g = vm.Module.Globals;
		for (int i = 0; i < g.Count; i++) if (ReferenceEquals(g[i], f)) return i;
		for (int i = 0; i < g.Count; i++)
			if (g[i].Name == f.Name || g[i].Name == owner.FullName + "." + f.Name || g[i].Name == owner.Name + "." + f.Name) return i;
		return f.Slot >= 0 && f.Slot < g.Count ? f.Slot : -1;
	}

	/// <summary>Оценка пригодности значения для параметра типа t: -1 — не подходит, больше — точнее.</summary>
	internal static int ParamScore(KValue v, KType t)
	{
		if (t is KPrimType { Kind: PrimKind.Object }) return 1;
		if (TypeChecks.IsInstance(v, t)) return 3;
		if (v.IsNumber && t is KPrimType p && NumericKind(p.Kind) is { } vk)
		{
			bool targetFloat = Num.IsFloat(vk);
			if (v.IsInteger) return targetFloat ? 1 : 2;
			return targetFloat ? 2 : -1;
		}
		return -1;
	}
}
