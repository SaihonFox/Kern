using Kern.Binding.Wrappers;
using Kern.Vm.Types;

namespace Kern.Lib.Di.Container;

public enum Lifetime
{
	/// <summary>Один экземпляр на весь корневой контейнер.</summary>
	Singleton,
	/// <summary>Один экземпляр на область (scope); в корне ведёт себя как singleton.</summary>
	Scoped,
	/// <summary>Новый экземпляр при каждом запросе.</summary>
	Transient,
}

/// <summary>Одна запись контейнера. На неё ссылаются все ключи-алиасы, поэтому экземпляр общий.</summary>
internal sealed class Registration(Lifetime life, KType implementation)
{
	public Lifetime Life { get; } = life;
	public KType Implementation { get; } = implementation;
	/// <summary>Фабрика `fun { … }` или `fun(container c) { … }`; null — создавать конструктором.</summary>
	public KFunc.Callable? Factory { get; init; }
	public bool FactoryTakesContainer { get; init; }
}

internal readonly record struct Key(KType Type, string Name);
