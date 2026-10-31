namespace Kern.Compiler.Semantics.Types;

/// <summary>Встроенные generic-типы с фиксированной арностью (spec §3, §14).</summary>
public enum BuiltinGenericKind
{
	List,
	Array,
	Dict,
	Set,
	Result,
	Task,
	Event,
	Observable,
	Channel,
	Pipe,
	Shared,

	/// <summary>Интерфейс итератора (spec §16): <c>iter&lt;T&gt;</c>.</summary>
	Iter,

	/// <summary>Асинхронный итератор (spec §16): <c>async_iter&lt;T&gt;</c>.</summary>
	AsyncIter,

	/// <summary>Единый корень коллекций (DECISIONS §C): <c>collection&lt;T&gt;</c>.</summary>
	Collection
}
