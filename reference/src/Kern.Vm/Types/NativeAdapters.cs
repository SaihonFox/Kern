using System.Collections.Concurrent;
using System.Reflection;
using Kern.Vm.Values;

namespace Kern.Vm.Types;

/// <summary>Фабрика адаптера: превращает значение VM (list/array/str/dict/range…) в объект нативного базового класса.</summary>
public interface IValueAdapterFactory
{
	/// <summary>null — значение этим типом не является.</summary>
	object? Adapt(KValue value);
}

/// <summary>Объект-адаптер хранит исходное значение VM: при возврате в Kern подставляется оно, а не обёртка.</summary>
public interface IValueAdapter
{
	KValue Original { get; }
}

/// <summary>
/// У нативного базового класса (например, collection&lt;T&gt;) могут быть «встроенные потомки», у которых нет C#-класса
/// (list, array, str, dict, range). Атрибут указывает фабрику адаптера: нативный код получает объект базового класса,
/// а проверка `is collection&lt;T&gt;` в VM принимает такие значения.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class KernAdapterAttribute(Type factory) : Attribute
{
	public Type Factory { get; } = factory;
}

public static class NativeAdapters
{
	private static readonly ConcurrentDictionary<Type, IValueAdapterFactory?> Factories = new();

	private static IValueAdapterFactory? FactoryFor(Type target)
	{
		var def = target.IsGenericType ? target.GetGenericTypeDefinition() : target;
		return Factories.GetOrAdd(def, d =>
			d.GetCustomAttribute<KernAdapterAttribute>(false) is { } a ? (IValueAdapterFactory?)Activator.CreateInstance(a.Factory) : null);
	}

	public static object? Adapt(KValue value, Type target) => FactoryFor(target)?.Adapt(value);

	public static bool Accepts(KValue value, Type target) => Adapt(value, target) is not null;
}
