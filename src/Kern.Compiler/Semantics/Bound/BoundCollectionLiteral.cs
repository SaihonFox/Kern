using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Bound;

/// <summary>
/// Элемент литерала коллекции: обычное значение или spread <c>..expr</c> (<c>IsSpread</c>).
/// Порядок элементов в литерале сохраняется как есть.
/// </summary>
public sealed record BoundCollectionItem(BoundExpression Value, bool IsSpread);

/// <summary>
/// Литерал коллекции (spec §3): <c>[a, ..b, c]</c> или <c>&lt;T&gt;[a, b]</c>. Тип —
/// <c>list&lt;T&gt;</c> либо <c>array&lt;T&gt;</c> (только для явного литерала с ожидаемым
/// <c>array&lt;T&gt;</c>), где <c>T</c> — тип элементов (<see cref="BoundCollectionLiteral.Type"/>).
/// Spread разворачивается на своём месте, поэтому <see cref="Items"/> — единый
/// упорядоченный список значений и spread'ов; в array-литерале spread не бывает.
/// </summary>
public sealed record BoundCollectionLiteral(
	IReadOnlyList<BoundCollectionItem> Items,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);

/// <summary>
/// Пара словарного литерала (spec §3): ключ и значение уже приведены к <c>K</c>/<c>V</c>.
/// </summary>
public sealed record BoundCollectionEntry(BoundExpression Key, BoundExpression Value);

/// <summary>
/// Литерал словаря (spec §3): <c>[k: v]</c> или <c>&lt;K: V&gt;[k: v]</c>. Тип — <c>dict&lt;K, V&gt;</c>.
/// Пары хранятся в порядке записи; повтор ключа не диагностируется — runtime оставляет
/// последнюю запись (<c>KDict.Set</c>).
/// </summary>
public sealed record BoundDictionaryLiteral(
	IReadOnlyList<BoundCollectionEntry> Entries,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);
