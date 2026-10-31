using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Reflection;

/// <summary>Описание reified-типа времени выполнения (аргументы generic учтены).</summary>
public sealed class TypeInfo
{
	private readonly Interpreter _vm;
	private IReadOnlyList<AttributeInfo>? _attrs;

	internal TypeInfo(Interpreter vm, KType type) { _vm = vm; Type = type; }

	public KType Type { get; }
	public string Name => Type is KClassType c ? c.Def.Name : Type.Name;
	public string FullName => Type.Name;

	/// <summary>Объявление типа в модуле (null для встроенных, функциональных, нативных типов).</summary>
	public TypeDef? Def => (Type as KClassType)?.Def;
	private KClassType? Class => Type as KClassType;

	public TypeKind? Kind => Def?.Kind;
	public bool IsDeclared => Def is not null;
	public bool IsEnum => Def?.Kind == TypeKind.Enum;
	public bool IsUnit => Def?.Kind is TypeKind.Unit or TypeKind.ImmutableUnit;
	public bool IsInterface => Def?.Kind == TypeKind.Interface;
	public bool IsAbstract => Def?.Kind == TypeKind.AbstractClass || Def?.Flags.HasFlag(MemberFlags.Abstract) == true;
	public MemberAccess Access => Def is null ? MemberAccess.Public : Reflector.AccessOf(Def.Flags, MemberAccess.Public);
	public IReadOnlyList<KType> GenericArguments => Class?.Args ?? [];

	// ---------- атрибуты ----------

	public IReadOnlyList<AttributeInfo> Attributes => _attrs ??= AttributeInfo.Wrap(_vm, Def?.Attributes ?? []);
	public bool HasAttribute(string name) => Def?.Attributes.Any(a => Reflector.NameMatches(a.TypeName, name)) == true;
	public AttributeInfo? GetAttribute(string name) => Attributes.FirstOrDefault(a => a.Is(name));

	// ---------- состав ----------

	public IReadOnlyList<TypeInfo> Parents =>
		Def is null ? [] : [.. Def.Parents.Select(p => Reflector.TypeOf(_vm, _vm.Types.Resolve(p, GenericArgumentsArray, [])))];

	private KType[] GenericArgumentsArray => Class?.Args ?? [];

	public IReadOnlyList<FieldInfo> Fields =>
		Def is null ? [] : [.. Def.Fields.Select(f => new FieldInfo(_vm, this, Def, f))];

	public IReadOnlyList<PropertyInfo> Properties =>
		Def is null ? [] : [.. Def.Properties.Select(p => new PropertyInfo(_vm, this, p))];

	public IReadOnlyList<MethodInfo> Methods =>
		Def is null ? [] : [.. Def.Methods.Select(i => Reflector.MethodOf(_vm, i, this))];

	public IReadOnlyList<MethodInfo> Constructors =>
		Def is null ? [] : [.. Def.Ctors.Select(i => Reflector.MethodOf(_vm, i, this))];

	public FieldInfo? GetField(string name) => Fields.FirstOrDefault(f => f.Name == name);
	public PropertyInfo? GetProperty(string name) => Properties.FirstOrDefault(p => p.Name == name);
	public IReadOnlyList<MethodInfo> GetMethods(string name) => [.. Methods.Where(m => m.Name == name)];

	/// <summary>Имена и значения членов enum.</summary>
	public IReadOnlyList<(string Name, long Value)> EnumMembers =>
		Def is null ? [] : [.. Def.EnumNames.Select((n, i) => (n, Def.EnumValues[i]))];

	// ---------- отношения типов ----------

	public bool IsInstance(KValue v) => _vm.IsInstance(v, Type);

	/// <summary>Присваиваемость: значение типа <paramref name="other"/> можно использовать как этот тип.</summary>
	public bool IsAssignableFrom(TypeInfo other)
	{
		if (ReferenceEquals(Type, other.Type)) return true;
		if (Type is KPrimType { Kind: PrimKind.Object }) return true;
		if (Type is KClassType me && other.Type is KClassType o)
		{
			if (!_vm.Info.IsSubtype(o.DefIndex, me.DefIndex)) return false;
			if (me.Args.Length == 0) return true;
			var args = TypeChecks.AncestorArgs(o, me.DefIndex);
			return args is not null && args.SequenceEqual(me.Args);
		}
		return false;
	}

	// ---------- создание и доступ ----------

	/// <summary>Создать экземпляр: подбирается конструктор по аргументам (по числу и типам).</summary>
	public KValue CreateInstance(params KValue[] args)
	{
		var cls = Class ?? throw new KernPanic($"тип {FullName} нельзя создать через рефлексию");
		if (IsAbstract || IsInterface) throw new KernPanic($"тип {FullName} абстрактный");
		if (cls.Def.GenericParams.Count > 0 && cls.Args.Length != cls.Def.GenericParams.Count)
			throw new KernPanic($"для создания {FullName} нужны аргументы типа");

		var obj = new KObject(cls, new KValue[_vm.Info.Classes[cls.DefIndex].FieldCount]);
		var self = KValue.Obj(obj);

		MethodInfo? best = null;
		int bestScore = -1;
		foreach (var c in Constructors)
		{
			if (!c.Accepts(args)) continue;
			int score = 0;
			for (int i = 0; i < args.Length && i < c.Parameters.Count; i++) score += Reflector.ParamScore(args[i], c.Parameters[i].Type);
			if (score > bestScore) { best = c; bestScore = score; }
		}
		if (best is null)
		{
			if (Constructors.Count == 0 && args.Length == 0) return self;
			// без конструкторов (динамические типы рефлексии): аргументы — значения полей по порядку объявления
			var own = Def!.Fields.Where(f => !f.Flags.HasFlag(MemberFlags.Static)).ToArray();
			if (Constructors.Count == 0 && args.Length <= own.Length)
			{
				for (int i = 0; i < args.Length; i++)
					obj.Fields[own[i].Slot] = Reflector.Coerce(args[i], _vm.Types.Resolve(own[i].Type, cls.Args, []));
				return self;
			}
			throw new KernPanic($"у {FullName} нет конструктора для {args.Length} арг.");
		}

		var coerced = new KValue[args.Length];
		for (int i = 0; i < args.Length; i++)
			coerced[i] = i < best.Parameters.Count ? Reflector.Coerce(args[i], best.Parameters[i].Type) : args[i];
		_vm.InvokeFunction(best.Index, coerced, self, true, [], cls.Args, []);
		return self;
	}

	/// <summary>Записать свойство или поле по имени (используется при инстанцировании атрибутов).</summary>
	public void SetMember(KObject target, string name, KValue value)
	{
		var self = KValue.Obj(target);
		if (GetProperty(name) is { } p) { p.SetValue(self, value); return; }
		if (GetField(name) is { } f) { f.SetValue(self, value); return; }
		throw new KernPanic($"у {FullName} нет члена '{name}'");
	}

	public KValue GetMember(KValue target, string name)
	{
		if (GetProperty(name) is { } p) return p.GetValue(target);
		if (GetField(name) is { } f) return f.GetValue(target);
		throw new KernPanic($"у {FullName} нет члена '{name}'");
	}

	public override string ToString() => FullName;
}
