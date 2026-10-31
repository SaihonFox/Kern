using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Reflection;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Lib.Di.Container;

/// <summary>
/// Контейнер зависимостей. Два способа описать связи (их можно смешивать):
/// <code>
/// // 1) регистрация типов и фабрик
/// var c = container()
/// c.singleton&lt;logger&gt;(fun { logger("app") })
/// c.transient&lt;repo&gt;(fun(container c) { repo(c.get&lt;logger&gt;()) })
/// c.singleton_as&lt;i_mailer, smtp_mailer&gt;()      // интерфейс → реализация
/// // 2) атрибуты
/// #[singleton] type clock { }
/// type service { #[inject] logger log { get; set } }
/// c.scan()                                         // зарегистрирует типы с #[singleton]/#[transient]/#[scoped]
/// var s = c.create&lt;service&gt;()                       // конструктор + #[inject]-свойства
/// </code>
/// Конструктор выбирается так: с атрибутом #[inject], иначе самый длинный из тех, чьи параметры можно разрешить.
/// </summary>
[KernType("container")]
public sealed class KernContainer
{
	private readonly KernContainer? _parent;
	private readonly Dictionary<Key, Registration> _registrations;
	private readonly Dictionary<Registration, KValue> _cache = [];
	private readonly List<KValue> _disposables = [];
	private readonly List<Key> _stack;

	[KernCtor] public KernContainer()
	{
		_registrations = [];
		_stack = [];
	}

	private KernContainer(KernContainer parent)
	{
		_parent = parent;
		_registrations = parent._registrations;
		_stack = parent._stack;
	}

	private KernContainer Root => _parent is null ? this : _parent.Root;

	/// <summary>Число зарегистрированных ключей.</summary>
	[KernProperty] public int Count => _registrations.Count;

	// ------------------------------------------------------------------ регистрация

	private KernContainer Add(Lifetime life, KType service, KType impl, string name, KFunc.Callable? factory, bool takesContainer)
	{
		_registrations[new Key(service, name)] = new Registration(life, impl) { Factory = factory, FactoryTakesContainer = takesContainer };
		return this;
	}

	private KernContainer Own(Lifetime l, KType[] ta, string name) => Add(l, Kv.Last(ta), Kv.Last(ta), name, null, false);
	private KernContainer With(Lifetime l, KType[] ta, string name, KFunc.Callable f, bool takes) => Add(l, Kv.Last(ta), Kv.Last(ta), name, f, takes);
	private KernContainer As(Lifetime l, KType[] ta, string name) =>
		ta.Length >= 2 ? Add(l, ta[^2], ta[^1], name, null, false) : throw new KernPanic("нужны два аргумента типа: <интерфейс, реализация>");

	[KernMember] public KernContainer Singleton<T>([KernTypeArgs] KType[] ta) => Own(Lifetime.Singleton, ta, "");
	[KernMember] public KernContainer Singleton<T>(string name, [KernTypeArgs] KType[] ta) => Own(Lifetime.Singleton, ta, name);
	[KernMember] public KernContainer Singleton<T>(KFunc<T> factory, [KernTypeArgs] KType[] ta) => With(Lifetime.Singleton, ta, "", factory.Inner, false);
	[KernMember] public KernContainer Singleton<T>(KFunc<T> factory, string name, [KernTypeArgs] KType[] ta) => With(Lifetime.Singleton, ta, name, factory.Inner, false);
	[KernMember] public KernContainer Singleton<T>(KFunc<KernContainer, T> factory, [KernTypeArgs] KType[] ta) => With(Lifetime.Singleton, ta, "", factory.Inner, true);
	[KernMember] public KernContainer Singleton<T>(KFunc<KernContainer, T> factory, string name, [KernTypeArgs] KType[] ta) => With(Lifetime.Singleton, ta, name, factory.Inner, true);

	[KernMember] public KernContainer Transient<T>([KernTypeArgs] KType[] ta) => Own(Lifetime.Transient, ta, "");
	[KernMember] public KernContainer Transient<T>(string name, [KernTypeArgs] KType[] ta) => Own(Lifetime.Transient, ta, name);
	[KernMember] public KernContainer Transient<T>(KFunc<T> factory, [KernTypeArgs] KType[] ta) => With(Lifetime.Transient, ta, "", factory.Inner, false);
	[KernMember] public KernContainer Transient<T>(KFunc<T> factory, string name, [KernTypeArgs] KType[] ta) => With(Lifetime.Transient, ta, name, factory.Inner, false);
	[KernMember] public KernContainer Transient<T>(KFunc<KernContainer, T> factory, [KernTypeArgs] KType[] ta) => With(Lifetime.Transient, ta, "", factory.Inner, true);
	[KernMember] public KernContainer Transient<T>(KFunc<KernContainer, T> factory, string name, [KernTypeArgs] KType[] ta) => With(Lifetime.Transient, ta, name, factory.Inner, true);

	[KernMember] public KernContainer Scoped<T>([KernTypeArgs] KType[] ta) => Own(Lifetime.Scoped, ta, "");
	[KernMember] public KernContainer Scoped<T>(string name, [KernTypeArgs] KType[] ta) => Own(Lifetime.Scoped, ta, name);
	[KernMember] public KernContainer Scoped<T>(KFunc<T> factory, [KernTypeArgs] KType[] ta) => With(Lifetime.Scoped, ta, "", factory.Inner, false);
	[KernMember] public KernContainer Scoped<T>(KFunc<T> factory, string name, [KernTypeArgs] KType[] ta) => With(Lifetime.Scoped, ta, name, factory.Inner, false);
	[KernMember] public KernContainer Scoped<T>(KFunc<KernContainer, T> factory, [KernTypeArgs] KType[] ta) => With(Lifetime.Scoped, ta, "", factory.Inner, true);
	[KernMember] public KernContainer Scoped<T>(KFunc<KernContainer, T> factory, string name, [KernTypeArgs] KType[] ta) => With(Lifetime.Scoped, ta, name, factory.Inner, true);

	/// <summary>Интерфейс/родитель → реализация: `c.singleton_as&lt;i_mailer, smtp_mailer&gt;()`.</summary>
	[KernMember] public KernContainer SingletonAs<TService, TImpl>([KernTypeArgs] KType[] ta) => As(Lifetime.Singleton, ta, "");
	[KernMember] public KernContainer SingletonAs<TService, TImpl>(string name, [KernTypeArgs] KType[] ta) => As(Lifetime.Singleton, ta, name);
	[KernMember] public KernContainer TransientAs<TService, TImpl>([KernTypeArgs] KType[] ta) => As(Lifetime.Transient, ta, "");
	[KernMember] public KernContainer TransientAs<TService, TImpl>(string name, [KernTypeArgs] KType[] ta) => As(Lifetime.Transient, ta, name);
	[KernMember] public KernContainer ScopedAs<TService, TImpl>([KernTypeArgs] KType[] ta) => As(Lifetime.Scoped, ta, "");
	[KernMember] public KernContainer ScopedAs<TService, TImpl>(string name, [KernTypeArgs] KType[] ta) => As(Lifetime.Scoped, ta, name);

	/// <summary>Готовый объект как singleton.</summary>
	[KernMember]
	public KernContainer Instance<T>(T value, [KernTypeArgs] KType[] ta) => InstanceCore(value, "", ta);

	[KernMember]
	public KernContainer Instance<T>(T value, string name, [KernTypeArgs] KType[] ta) => InstanceCore(value, name, ta);

	private KernContainer InstanceCore<T>(T value, string name, KType[] ta)
	{
		var kv = Kv.Of(value);
		var type = Kv.Last(ta);
		var reg = new Registration(Lifetime.Singleton, type) { Factory = null };
		_registrations[new Key(type, name)] = reg;
		Root._cache[reg] = kv;
		return this;
	}

	/// <summary>Регистрирует все объявленные типы с атрибутами #[singleton] / #[transient] / #[scoped]: под своим именем и под каждым родителем.</summary>
	[KernMember]
	public KernContainer Scan([KernVm] Interpreter vm)
	{
		foreach (var ti in Reflector.DeclaredTypes(vm))
		{
			if (ti.IsAbstract || ti.IsInterface || ti.IsEnum || ti.Def is null || ti.Def.GenericParams.Count > 0) continue;
			Lifetime? life = ti.HasAttribute("singleton") ? Lifetime.Singleton
				: ti.HasAttribute("transient") ? Lifetime.Transient
				: ti.HasAttribute("scoped") ? Lifetime.Scoped : null;
			if (life is null) continue;
			var reg = new Registration(life.Value, ti.Type);
			_registrations[new Key(ti.Type, "")] = reg;
			foreach (var p in ti.Parents)
				if (p.IsDeclared) _registrations[new Key(p.Type, "")] = reg;
		}
		return this;
	}

	// ------------------------------------------------------------------ получение

	[KernMember] public bool Has<T>([KernTypeArgs] KType[] ta) => Find(new Key(Kv.Last(ta), "")) is not null;
	[KernMember] public bool Has<T>(string name, [KernTypeArgs] KType[] ta) => Find(new Key(Kv.Last(ta), name)) is not null;

	[KernMember] public T Get<T>([KernVm] Interpreter vm, [KernTypeArgs] KType[] ta) => Kv.As<T>(Resolve(vm, Kv.Last(ta), "", true));
	[KernMember] public T Get<T>(string name, [KernVm] Interpreter vm, [KernTypeArgs] KType[] ta) => Kv.As<T>(Resolve(vm, Kv.Last(ta), name, true));

	/// <summary>nil, если тип не зарегистрирован.</summary>
	[KernMember] public T? TryGet<T>([KernVm] Interpreter vm, [KernTypeArgs] KType[] ta) => Kv.As<T?>(Resolve(vm, Kv.Last(ta), "", false));
	[KernMember] public T? TryGet<T>(string name, [KernVm] Interpreter vm, [KernTypeArgs] KType[] ta) => Kv.As<T?>(Resolve(vm, Kv.Last(ta), name, false));

	/// <summary>Создаёт объект типа T (регистрация не нужна): конструктор с зависимостями + #[inject]-члены.</summary>
	[KernMember]
	public T Create<T>([KernVm] Interpreter vm, [KernTypeArgs] KType[] ta) => Kv.As<T>(Build(vm, Kv.Last(ta), this));

	/// <summary>Заполняет #[inject]-свойства и поля уже существующего объекта.</summary>
	[KernMember]
	public T Inject<T>(T value, [KernVm] Interpreter vm)
	{
		InjectMembers(vm, Kv.Of(value), this);
		return value;
	}

	/// <summary>Дочерняя область: scoped-объекты живут в ней, singleton — общие с родителем.</summary>
	[KernMember] public KernContainer Scope() => new(this);

	/// <summary>Освобождает созданные в этой области объекты, у которых есть `dispose()` (в обратном порядке создания).</summary>
	[KernMember]
	public void Dispose([KernVm] Interpreter vm)
	{
		for (int i = _disposables.Count - 1; i >= 0; i--)
			DisposeMethod(vm, _disposables[i])?.Invoke(_disposables[i]);
		_disposables.Clear();
		_cache.Clear();
	}

	/// <summary>Для других библиотек (backend): зарегистрированный тип берётся из контейнера, иначе создаётся с внедрением зависимостей.</summary>
	public KValue CreateByType(Interpreter vm, KType type) =>
		Find(new Key(type, "")) is not null ? Resolve(vm, type, "", true) : Build(vm, type, this);

	// ------------------------------------------------------------------ реализация

	private Registration? Find(Key key) => _registrations.GetValueOrDefault(key);

	private KValue Resolve(Interpreter vm, KType type, string name, bool required)
	{
		var key = new Key(type, name);
		var reg = Find(key);
		if (reg is null)
		{
			if (required) throw new KernPanic($"di: тип {Describe(key)} не зарегистрирован");
			return KValue.Nil;
		}
		if (_stack.Contains(key))
			throw new KernPanic("di: циклическая зависимость: " + string.Join(" → ", _stack.SkipWhile(k => k != key).Append(key).Select(Describe)));
		_stack.Add(key);
		try
		{
			switch (reg.Life)
			{
				case Lifetime.Singleton:
					return FromCache(Root, vm, reg);
				case Lifetime.Scoped:
					return FromCache(this, vm, reg);
				default:
					return Make(vm, reg, this);
			}
		}
		finally { _stack.RemoveAt(_stack.Count - 1); }
	}

	private static KValue FromCache(KernContainer owner, Interpreter vm, Registration reg)
	{
		if (owner._cache.TryGetValue(reg, out var v)) return v;
		v = Make(vm, reg, owner);
		owner._cache[reg] = v;
		return v;
	}

	private static KValue Make(Interpreter vm, Registration reg, KernContainer owner)
	{
		KValue v;
		if (reg.Factory is { } f)
		{
			v = reg.FactoryTakesContainer ? f.Invoke([KValue.Obj(owner)]) : f.Invoke([]);
			InjectMembers(vm, v, owner);
		}
		else v = Build(vm, reg.Implementation, owner);
		owner.Track(vm, v);
		return v;
	}

	private void Track(Interpreter vm, KValue v)
	{
		if (v.Ref is KObject && DisposeMethod(vm, v) is not null) _disposables.Add(v);
	}

	private static MethodInfo? DisposeMethod(Interpreter vm, KValue v)
	{
		if (v.Ref is not KObject) return null;
		return Reflector.TypeOf(vm, v).GetMethods("dispose").FirstOrDefault(m => m.Parameters.Count == 0);
	}

	private static KValue Build(Interpreter vm, KType type, KernContainer owner)
	{
		var ti = Reflector.TypeOf(vm, type);
		if (!ti.IsDeclared) throw new KernPanic($"di: {ti.FullName} нельзя создать (нужен объявленный тип)");
		if (ti.IsAbstract || ti.IsInterface) throw new KernPanic($"di: {ti.Name} абстрактный — зарегистрируйте реализацию (singleton_as<{ti.Name}, …>)");

		var args = new List<KValue>();
		var ctor = ChooseConstructor(ti, owner);
		if (ctor is not null)
			foreach (var p in ctor.Parameters)
			{
				var v = owner.Resolve(vm, p.Type, "", !p.HasDefault);
				if (v.IsNil && p.HasDefault) break;
				args.Add(v);
			}
		var obj = ti.CreateInstance([.. args]);
		InjectMembers(vm, obj, owner);
		return obj;
	}

	private static MethodInfo? ChooseConstructor(TypeInfo ti, KernContainer owner)
	{
		var ctors = ti.Constructors;
		if (ctors.Count == 0) return null;
		var marked = ctors.FirstOrDefault(c => c.HasAttribute("inject"));
		if (marked is not null) return marked;
		return ctors
			.OrderByDescending(c => c.Parameters.Count)
			.FirstOrDefault(c => c.Parameters.All(p => p.HasDefault || p.IsVariadic || owner.Find(new Key(p.Type, "")) is not null))
			?? ctors.OrderBy(c => c.Parameters.Count).First();
	}

	private static void InjectMembers(Interpreter vm, KValue obj, KernContainer owner)
	{
		if (obj.Ref is not KObject) return;
		var ti = Reflector.TypeOf(vm, obj);
		foreach (var p in ti.Properties)
		{
			if (p.IsStatic || !p.HasAttribute("inject") || !(p.CanWrite || p.IsInitOnly)) continue;
			var (name, optional) = InjectArgs(p.Attributes);
			var v = owner.Resolve(vm, p.Type, name, !optional);
			if (!(optional && v.IsNil)) p.SetValue(obj, v);
		}
		foreach (var f in ti.Fields)
		{
			if (f.IsStatic || !f.HasAttribute("inject")) continue;
			var (name, optional) = InjectArgs(f.Attributes);
			var v = owner.Resolve(vm, f.Type, name, !optional);
			if (!(optional && v.IsNil)) f.SetValue(obj, v);
		}
	}

	/// <summary>`#[inject]`, `#[inject("имя")]`, `#[inject(optional: true)]`.</summary>
	private static (string Name, bool Optional) InjectArgs(IReadOnlyList<AttributeInfo> attrs)
	{
		var a = attrs.FirstOrDefault(x => x.Is("inject"));
		if (a is null) return ("", false);
		string name = a.Positional.Count > 0 && a.Positional[0].Kind == ValueKind.Str ? a.Positional[0].AsStr : "";
		bool optional = a.Named.TryGetValue("optional", out var o) && o.AsBool;
		return (name, optional);
	}

	private static string Describe(Key k) => k.Name.Length == 0 ? k.Type.Name : $"{k.Type.Name}(\"{k.Name}\")";
}
