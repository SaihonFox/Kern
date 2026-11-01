namespace Kern.Binding.Attributes;

/// <summary>
/// Привязывает C#-класс к Kern-типу. Если класс статический, его члены с
/// <see cref="KernMemberAttribute"/>/<see cref="KernPropertyAttribute"/> становятся членами
/// Kern-типа <paramref name="name"/> (первый параметр — <c>self</c>); иначе экземпляр
/// хранится в <c>KNativeBox</c>, а <see cref="KernCtorAttribute"/> задаёт конструктор.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class KernTypeAttribute(string name) : Attribute
{
	/// <summary>Имя Kern-типа (например, <c>str</c>, <c>Counter</c>).</summary>
	public string Name { get; } = name;

	/// <summary>Тип встроенный: его имя нельзя переопределять обычным <c>type</c>.</summary>
	public bool IsBuiltin { get; init; }

	/// <summary>
	/// Арность generic-параметров Kern-типа (<c>set&lt;T&gt;</c> — 1). Нужна не-generic
	/// C#-классам-дескрипторам: члены всё равно принимают <c>KValue</c>/<c>KNativeBox</c>,
	/// а generic-аргументы подставляет компилятор (spec §16).
	/// </summary>
	public int GenericArity { get; init; }
}

/// <summary>Явное Kern-имя члена/параметра; по умолчанию C#-имя переводится в <c>snake_case</c>.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
	AllowMultiple = false, Inherited = false)]
public sealed class KernNameAttribute(string name) : Attribute
{
	public string Name { get; } = name;
}

/// <summary>Метод становится обычной функцией Kern (метод экземпляра или расширение).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class KernMemberAttribute : Attribute
{
	/// <summary>Перегрузка помечена как оператор; текст оператора, например <c>+</c>.</summary>
	public string? Operator { get; init; }
}

/// <summary>C#-свойство или статический геттер/сеттер с <c>self</c> становится свойством Kern.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class KernPropertyAttribute : Attribute
{
	/// <summary>Только чтение (сеттер не публикуется в Kern).</summary>
	public bool GetOnly { get; init; }

	/// <summary>Только запись.</summary>
	public bool SetOnly { get; init; }
}

/// <summary>Член статический (для нестатического класса — вызов без экземпляра).</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class KernStaticAttribute : Attribute
{
}

/// <summary>
/// Конструктор Kern-типа. У C#-конструктора — фабрика нативного handle-типа; у статического
/// метода модуля — фабрика встроенного generic-контейнера (<c>set&lt;T&gt;()</c> и т.п.).
/// Параметры C# должны иметь имена в <c>snake_case</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class KernCtorAttribute : Attribute
{
}

/// <summary>
/// Помечает последний параметр как массив reified-аргументов типа
/// (<c>KType[]</c>); компилятор передаёт сюда <c>typeof(T)</c> вызывающего кода.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class KernTypeArgsAttribute : Attribute
{
}

/// <summary>
/// Переопределяет Kern-текст возвращаемого типа нативного метода (spec §12.4). Нужен там,
/// где CLR-сигнатура не выражает Kern-тип: например <c>json.from_str&lt;T&gt;</c> собирает
/// <c>Ok&lt;T&gt;</c>/<c>Err&lt;str&gt;</c> вручную и возвращает <c>KValue</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class KernReturnAttribute(string type) : Attribute
{
	public string Type { get; } = type;
}

/// <summary>Адаптер встроенного типа Kern к нативному представлению.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class KernAdapterAttribute(Type adapterType) : Attribute
{
	public Type AdapterType { get; } = adapterType;
}

/// <summary>Член не виден из Kern (служебный).</summary>
[AttributeUsage(AttributeTargets.All, AllowMultiple = false, Inherited = false)]
public sealed class KernIgnoreAttribute : Attribute
{
}

/// <summary>
/// Привязывает статический C#-класс к Kern-модулю: его <see cref="KernMemberAttribute"/>-
/// методы без <c>self</c> становятся top-level функциями модуля (например, <c>kern.math</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class KernModuleAttribute(string name) : Attribute
{
	public string Name { get; } = name;
}
