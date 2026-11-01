using Kern.Binding.Attributes;
using Kern.Binding.Marshalling;
using Kern.Binding.Values;
using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Lib.Di;

internal enum Lifetime
{
	Singleton,
	Scoped,
	Transient
}

internal sealed class Registration(Lifetime lifetime, KType implementation)
{
	public Lifetime Lifetime { get; } = lifetime;
	public KType Implementation { get; } = implementation;
	public KValue? Factory { get; init; }
	public bool FactoryTakesContainer { get; init; }
}

internal readonly record struct Key(KType Type, string Name);

/// <summary>
/// Контейнер внедрения зависимостей (spec round 20): регистрации singleton/transient/scoped,
/// фабрики, сканирование #[singleton]/#[transient]/#[scoped] и внедрение #[inject].
/// </summary>
[KernType("container")]
public sealed class KernContainer
{
	private readonly KernContainer? _parent;
	private readonly Dictionary<Key, Registration> _registrations;
	private readonly Dictionary<Registration, KValue> _cache = [];
	private readonly List<KValue> _disposables = [];
	private readonly List<Key> _stack;

	public KernContainer()
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

	[KernCtor]
	[KernName("container")]
	public static KernContainer Create() => new();

	[KernProperty]
	public static int Count(KNativeBox self) => Require(self)._registrations.Count;

	// ---------------------------------------------------------------- registration

	private KernContainer Add(
		Lifetime lifetime,
		KType service,
		KType implementation,
		string name,
		KValue? factory,
		bool takesContainer)
	{
		_registrations[new Key(service, name)] = new Registration(lifetime, implementation)
		{
			Factory = factory,
			FactoryTakesContainer = takesContainer,
		};
		return this;
	}

	private KernContainer Own(Lifetime lifetime, KType[] types, string name) =>
		Add(lifetime, Last(types), Last(types), name, null, false);

	private KernContainer With(Lifetime lifetime, KType[] types, string name, KValue factory, bool takes) =>
		Add(lifetime, Last(types), Last(types), name, factory, takes);

	private KernContainer As(Lifetime lifetime, KType[] types, string name) =>
		types.Length >= 2
			? Add(lifetime, types[^2], types[^1], name, null, false)
			: throw new KernPanic("нужны два аргумента типа: <сервис, реализация>");

	[KernMember]
	public static KernContainer Singleton<T>(Interpreter interpreter, KNativeBox self, [KernTypeArgs] KType[] types) =>
		Require(self).Own(Lifetime.Singleton, types, "");

	[KernMember]
	public static KernContainer Singleton<T>(Interpreter interpreter, KNativeBox self, string name, [KernTypeArgs] KType[] types) =>
		Require(self).Own(Lifetime.Singleton, types, name);

	[KernMember]
	public static KernContainer Singleton<T>(Interpreter interpreter, KNativeBox self, KValue factory, [KernTypeArgs] KType[] types) =>
		Require(self).With(Lifetime.Singleton, types, "", factory, TakesContainer(factory));

	[KernMember]
	public static KernContainer Singleton<T>(Interpreter interpreter, KNativeBox self, KValue factory, string name, [KernTypeArgs] KType[] types) =>
		Require(self).With(Lifetime.Singleton, types, name, factory, TakesContainer(factory));

			[KernMember]
	public static KernContainer Transient<T>(Interpreter interpreter, KNativeBox self, [KernTypeArgs] KType[] types) =>
		Require(self).Own(Lifetime.Transient, types, "");

	[KernMember]
	public static KernContainer Transient<T>(Interpreter interpreter, KNativeBox self, string name, [KernTypeArgs] KType[] types) =>
		Require(self).Own(Lifetime.Transient, types, name);

	[KernMember]
	public static KernContainer Transient<T>(Interpreter interpreter, KNativeBox self, KValue factory, [KernTypeArgs] KType[] types) =>
		Require(self).With(Lifetime.Transient, types, "", factory, TakesContainer(factory));

	[KernMember]
	public static KernContainer Transient<T>(Interpreter interpreter, KNativeBox self, KValue factory, string name, [KernTypeArgs] KType[] types) =>
		Require(self).With(Lifetime.Transient, types, name, factory, TakesContainer(factory));

			[KernMember]
	public static KernContainer Scoped<T>(Interpreter interpreter, KNativeBox self, [KernTypeArgs] KType[] types) =>
		Require(self).Own(Lifetime.Scoped, types, "");

	[KernMember]
	public static KernContainer Scoped<T>(Interpreter interpreter, KNativeBox self, string name, [KernTypeArgs] KType[] types) =>
		Require(self).Own(Lifetime.Scoped, types, name);

	[KernMember]
	public static KernContainer Scoped<T>(Interpreter interpreter, KNativeBox self, KValue factory, [KernTypeArgs] KType[] types) =>
		Require(self).With(Lifetime.Scoped, types, "", factory, TakesContainer(factory));

	[KernMember]
	public static KernContainer Scoped<T>(Interpreter interpreter, KNativeBox self, KValue factory, string name, [KernTypeArgs] KType[] types) =>
		Require(self).With(Lifetime.Scoped, types, name, factory, TakesContainer(factory));

			[KernMember]
	public static KernContainer SingletonAs<TService, TImpl>(Interpreter interpreter, KNativeBox self, [KernTypeArgs] KType[] types) =>
		Require(self).As(Lifetime.Singleton, types, "");

	[KernMember]
	public static KernContainer SingletonAs<TService, TImpl>(Interpreter interpreter, KNativeBox self, string name, [KernTypeArgs] KType[] types) =>
		Require(self).As(Lifetime.Singleton, types, name);

	[KernMember]
	public static KernContainer TransientAs<TService, TImpl>(Interpreter interpreter, KNativeBox self, [KernTypeArgs] KType[] types) =>
		Require(self).As(Lifetime.Transient, types, "");

	[KernMember]
	public static KernContainer TransientAs<TService, TImpl>(Interpreter interpreter, KNativeBox self, string name, [KernTypeArgs] KType[] types) =>
		Require(self).As(Lifetime.Transient, types, name);

	[KernMember]
	public static KernContainer ScopedAs<TService, TImpl>(Interpreter interpreter, KNativeBox self, [KernTypeArgs] KType[] types) =>
		Require(self).As(Lifetime.Scoped, types, "");

	[KernMember]
	public static KernContainer ScopedAs<TService, TImpl>(Interpreter interpreter, KNativeBox self, string name, [KernTypeArgs] KType[] types) =>
		Require(self).As(Lifetime.Scoped, types, name);

	[KernMember]
	public static KernContainer Instance<T>(Interpreter interpreter, KNativeBox self, KValue value, [KernTypeArgs] KType[] types) =>
		Require(self).InstanceCore(value, "", types);

	[KernMember]
	public static KernContainer Instance<T>(Interpreter interpreter, KNativeBox self, KValue value, string name, [KernTypeArgs] KType[] types) =>
		Require(self).InstanceCore(value, name, types);

	private KernContainer InstanceCore(KValue value, string name, KType[] types)
	{
		KType type = Last(types);
		var registration = new Registration(Lifetime.Singleton, type);
		_registrations[new Key(type, name)] = registration;
		Root._cache[registration] = value;
		return this;
	}

	/// <summary>Зарегистрировать все объявленные типы с #[singleton]/#[transient]/#[scoped].</summary>
	[KernMember]
	public static KernContainer Scan(Interpreter interpreter, KNativeBox self)
	{
		KernContainer container = Require(self);
		foreach (TypeDef definition in interpreter.Module.TypePool)
		{
			if (definition.Kind == TypeDefKind.Union ||
				definition.GenericParams.Count > 0 ||
				(definition.Flags & MemberFlags.Abstract) != 0)
			{
				continue;
			}

			Lifetime? lifetime = Attribute(definition.Attributes, "singleton") is not null ? Lifetime.Singleton
				: Attribute(definition.Attributes, "transient") is not null ? Lifetime.Transient
				: Attribute(definition.Attributes, "scoped") is not null ? Lifetime.Scoped
				: null;
			if (lifetime is null)
				continue;

			KClassType type = interpreter.Types.Named(definition);
			var registration = new Registration(lifetime.Value, type);
			container._registrations[new Key(type, "")] = registration;
			foreach (TypeSig parent in definition.Parents)
			{
				try
				{
					if (interpreter.Types.FromSig(parent, type) is KClassType parentType)
						container._registrations[new Key(parentType, "")] = registration;
				}
				catch (InvalidOperationException)
				{
					// Повреждённая ссылка на родителя: пропускаем.
				}
			}
		}

		return container;
	}

	// ---------------------------------------------------------------- lookup

	[KernMember]
	public static bool Has<T>(Interpreter interpreter, KNativeBox self, [KernTypeArgs] KType[] types) =>
		Require(self).Find(new Key(Last(types), "")) is not null;

	[KernMember]
	public static bool Has<T>(Interpreter interpreter, KNativeBox self, string name, [KernTypeArgs] KType[] types) =>
		Require(self).Find(new Key(Last(types), name)) is not null;

	[KernMember]
	[KernReturn("T")]
	public static KValue Get<T>(Interpreter interpreter, KNativeBox self, [KernTypeArgs] KType[] types) =>
		Require(self).Resolve(interpreter, Last(types), "", required: true);

	[KernMember]
	[KernReturn("T")]
	public static KValue Get<T>(Interpreter interpreter, KNativeBox self, string name, [KernTypeArgs] KType[] types) =>
		Require(self).Resolve(interpreter, Last(types), name, required: true);

	[KernMember]
	[KernReturn("T?")]
	public static KValue TryGet<T>(Interpreter interpreter, KNativeBox self, [KernTypeArgs] KType[] types) =>
		Require(self).Resolve(interpreter, Last(types), "", required: false);

	[KernMember]
	[KernReturn("T?")]
	public static KValue TryGet<T>(Interpreter interpreter, KNativeBox self, string name, [KernTypeArgs] KType[] types) =>
		Require(self).Resolve(interpreter, Last(types), name, required: false);

	[KernMember]
	[KernReturn("T")]
	public static KValue Create<T>(Interpreter interpreter, KNativeBox self, [KernTypeArgs] KType[] types) =>
		Build(interpreter, Last(types), Require(self));

	[KernMember]
	[KernReturn("T")]
	public static KValue Inject<T>(Interpreter interpreter, KNativeBox self, KValue value, [KernTypeArgs] KType[] types)
	{
		KernContainer container = Require(self);
		InjectMembers(interpreter, value, container);
		return value;
	}

	[KernMember]
	public static KernContainer Scope(Interpreter interpreter, KNativeBox self) =>
		Require(self).CreateScope();

	[KernMember]
	public static void Dispose(Interpreter interpreter, KNativeBox self) =>
		Require(self).DisposeAll(interpreter);

	/// <summary>Для backend: корневой контейнер без Kern-значения.</summary>
	public static KernContainer CreateRoot() => new();

	/// <summary>Дочерняя область (scoped-сервисы живут в ней).</summary>
	public KernContainer CreateScope() => new(this);

	/// <summary>Вызвать <c>dispose()</c> у созданных в области объектов в обратном порядке.</summary>
	public void DisposeAll(Interpreter interpreter)
	{
		for (var index = _disposables.Count - 1; index >= 0; index--)
		{
			KValue value = _disposables[index];
			if (DisposeMethod(interpreter, value) is { } dispose)
				_ = interpreter.RunValue(KValue.Obj(dispose), [value]);
		}

		_disposables.Clear();
		_cache.Clear();
	}

	/// <summary>Для backend/CLI: получить зарегистрированный сервис или создать конкретный тип.</summary>
	public KValue CreateByType(Interpreter interpreter, KType type) =>
		Find(new Key(type, "")) is not null
			? Resolve(interpreter, type, "", required: true)
			: Build(interpreter, type, this);

	// ---------------------------------------------------------------- resolution

	private Registration? Find(Key key) => _registrations.GetValueOrDefault(key);

	private KValue Resolve(Interpreter interpreter, KType type, string name, bool required)
	{
		var key = new Key(type, name);
		Registration? registration = Find(key);
		if (registration is null)
		{
			if (required)
				throw new KernPanic($"di: тип {Describe(key)} не зарегистрирован");
			return KValue.Nil;
		}

		if (_stack.Contains(key))
			throw new KernPanic("di: циклическая зависимость: " +
				string.Join(" -> ", _stack.SkipWhile(item => item != key).Append(key).Select(Describe)));
		_stack.Add(key);
		try
		{
			return registration.Lifetime switch
			{
				Lifetime.Singleton => FromCache(Root, interpreter, registration),
				Lifetime.Scoped => FromCache(this, interpreter, registration),
				_ => Make(interpreter, registration, this),
			};
		}
		finally
		{
			_stack.RemoveAt(_stack.Count - 1);
		}
	}

	private static KValue FromCache(KernContainer owner, Interpreter interpreter, Registration registration)
	{
		if (owner._cache.TryGetValue(registration, out KValue cached))
			return cached;
		KValue value = Make(interpreter, registration, owner);
		owner._cache[registration] = value;
		return value;
	}

	private static KValue Make(Interpreter interpreter, Registration registration, KernContainer owner)
	{
		KValue value;
		if (registration.Factory is { } factory)
		{
			value = registration.FactoryTakesContainer
				? interpreter.RunValue(factory, KValue.Obj(new KNativeBox(owner)))
				: interpreter.RunValue(factory);
			InjectMembers(interpreter, value, owner);
		}
		else
		{
			value = Build(interpreter, registration.Implementation, owner);
		}

		owner.Track(interpreter, value);
		return value;
	}

	private void Track(Interpreter interpreter, KValue value)
	{
		if (value.Ref is KObject && DisposeMethod(interpreter, value) is not null)
			_disposables.Add(value);
	}

	private static FunctionDef? DisposeMethod(Interpreter interpreter, KValue value)
	{
		if (value.Ref is not KObject instance)
			return null;
		for (TypeDef? definition = instance.Type.Definition; definition is not null;)
		{
			foreach (int index in definition.Methods)
			{
				FunctionDef method = interpreter.Module.FunctionPool[index];
				if (method.Name == "dispose" && (method.Flags & MemberFlags.Static) == 0 && method.ParamCount == 1)
					return method;
			}

			break;
		}

		return null;
	}

	private static KValue Build(Interpreter interpreter, KType type, KernContainer owner)
	{
		if (type is not KClassType classType)
			throw new KernPanic($"di: нельзя создать '{type}' — ожидался объявленный type/unit");
		TypeDef definition = classType.Definition;
		if ((definition.Flags & MemberFlags.Abstract) != 0 || definition.Kind == TypeDefKind.Union)
			throw new KernPanic($"di: {definition.Name} абстрактный — зарегистрируйте реализацию (singleton_as<{definition.Name}, .>)");

		var fields = new KValue[definition.Fields.Count];
		Array.Fill(fields, KValue.None);
		var instance = new KObject(classType, fields);
		KValue value = KValue.Obj(instance);

		FunctionDef? constructor = ChooseConstructor(interpreter, definition);
		if (constructor is not null)
		{
			var arguments = new List<KValue>(constructor.ParamCount) { value };
			for (var index = 1; index < constructor.ParamCount; index++)
			{
				KType parameterType = interpreter.Types.FromSig(constructor.ParamTypes[index], classType);
				KValue dependency = owner.Resolve(interpreter, parameterType, "", required: true);
				arguments.Add(dependency);
			}

			_ = interpreter.RunValue(KValue.Obj(constructor), [.. arguments]);
		}

		InjectMembers(interpreter, value, owner);
		return value;
	}

	private static FunctionDef? ChooseConstructor(Interpreter interpreter, TypeDef definition)
	{
		var constructors = definition.Ctors
			.Select(index => interpreter.Module.FunctionPool[index])
			.Where(function => (function.Flags & MemberFlags.Ctor) != 0)
			.ToList();
		if (constructors.Count == 0)
			return null;
		if (constructors.Count == 1)
			return constructors[0];
		FunctionDef? marked = constructors.FirstOrDefault(function =>
			Attribute(function.Attributes, "inject") is not null);
		return marked ?? constructors.OrderByDescending(function => function.ParamCount).First();
	}

	private static void InjectMembers(Interpreter interpreter, KValue value, KernContainer owner)
	{
		if (value.Ref is not KObject instance)
			return;
		TypeDef definition = instance.Type.Definition;
		instance.BeginConstruction();
		try
		{
			foreach (FieldDef field in definition.Fields)
			{
				if (Attribute(field.Attributes, "inject") is not { } inject)
					continue;
				if (!TryInject(inject, out string name, out bool optional))
					continue;
				KType type = interpreter.Types.FromSig(field.Type, instance.Type);
				KValue dependency = owner.Resolve(interpreter, type, name, required: !optional);
				if (dependency.IsNilOrNone && optional)
					continue;
				instance.SetField(field.Slot, dependency);
			}

			foreach (PropertyDef property in definition.Properties)
			{
				if (Attribute(property.Attributes, "inject") is not { } inject)
					continue;
				if (!TryInject(inject, out string name, out bool optional))
					continue;
				KType type = interpreter.Types.FromSig(property.Type, instance.Type);
				KValue dependency = owner.Resolve(interpreter, type, name, required: !optional);
				if (dependency.IsNilOrNone && optional)
					continue;
				if (property.BackingField >= 0)
					instance.SetField(property.BackingField, dependency);
				else if (property.Setter >= 0)
					_ = interpreter.RunValue(KValue.Obj(interpreter.Module.FunctionPool[property.Setter]), [value, dependency]);
				else
					throw new KernPanic($"di: свойство '{property.Name}' без setter/backing-поля нельзя внедрить");
			}
		}
		finally
		{
			instance.EndConstruction();
		}
	}

	private static bool TryInject(AttributeUse attribute, out string name, out bool optional)
	{
		name = "";
		optional = false;
		foreach (KConst argument in attribute.Args)
		{
			string text = argument.Kind == KConstKind.Str ? argument.AsStr : argument.ToString();
			if (text.StartsWith("optional", StringComparison.Ordinal))
			{
				optional = text.Contains("true", StringComparison.Ordinal);
				continue;
			}

			if (text.Length > 0)
				name = text;
		}

		return true;
	}

	private static AttributeUse? Attribute(IEnumerable<AttributeUse> attributes, string name) =>
		attributes.FirstOrDefault(attribute => attribute.Name == name);

	private static KType Last(KType[] types) =>
		types.Length > 0 ? types[^1] : throw new KernPanic("di: не передан аргумент типа");

	private static bool TakesContainer(KValue factory) => factory.Ref switch
	{
		KClosure closure => closure.Function.ParamCount == 1,
		FunctionDef function => function.ParamCount == 1,
		_ => false,
	};

	private static string Describe(Key key) =>
		key.Name.Length == 0 ? key.Type.Canonical : $"{key.Type.Canonical} '{key.Name}'";

	private static KernContainer Require(KNativeBox self) => self.Value switch
	{
		KernContainer container => container,
		_ => throw new KernPanic($"container: ожидался контейнер, получено {self.Value.GetType().Name}"),
	};
}
