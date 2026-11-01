namespace Kern.Lib.Transpile.Targets.Kotlin;

/// <summary>Вспомогательный runtime, который кладётся рядом с кодом Kotlin-проекта.</summary>
internal static class KotlinRuntime
{
	public const string Text = """
// Сгенерировано транспилятором Kern → Kotlin. Не редактируйте вручную.
package kern.runtime

class KResult<T, E> private constructor(
	val isOk: Boolean,
	val value: T?,
	val error: E?,
) {
	companion object {
		fun <T, E> ok(value: T): KResult<T, E> = KResult(true, value, null)

		fun <T, E> err(error: E): KResult<T, E> = KResult(false, null, error)
	}
}

fun kernLen(value: Any?): Int = when (value) {
	is String -> value.length
	is Collection<*> -> value.size
	is Map<*, *> -> value.size
	is Array<*> -> value.size
	else -> 0
}

fun kernSlice(value: Any?, start: Int, end: Int, inclusive: Boolean): Any? = when (value) {
	is List<*> -> {
		val from = start.coerceIn(0, value.size)
		val to = (if (inclusive) end + 1 else end).coerceIn(from, value.size)
		value.subList(from, to).toMutableList()
	}

	is String -> {
		val from = start.coerceIn(0, value.length)
		val to = (if (inclusive) end + 1 else end).coerceIn(from, value.length)
		value.substring(from, to)
	}

	else -> value
}

fun kernIn(item: Any?, collection: Any?): Boolean = when (collection) {
	is Map<*, *> -> collection.containsKey(item)
	is Iterable<*> -> collection.contains(item)
	is Array<*> -> collection.contains(item)
	is String -> collection.contains(item.toString())
	else -> false
}

fun kernPow(left: Int, right: Int): Int = Math.pow(left.toDouble(), right.toDouble()).toInt()

fun kernPow(left: Long, right: Long): Long = Math.pow(left.toDouble(), right.toDouble()).toLong()

fun kernPow(left: Double, right: Double): Double = Math.pow(left, right)

fun kernFormat(value: Any?): String = value.toString()

fun kernFormat(value: Any?, spec: String): String = value.toString()

fun kernHash(value: Any?): ULong = (value?.hashCode() ?: 0).toULong()

fun <T : Comparable<T>> kernCompare(left: T, right: T): Int = left.compareTo(right)

fun <T> kernClone(value: T): T = value

fun kernTypeOf(value: Any?): Any = value?.let { it::class } ?: Any::class
""";
}
