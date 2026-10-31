namespace Kern.Lib.Transpile.Targets.Kotlin;

internal static class KotlinRuntime
{
	public const string Text = """
// Вспомогательные типы для кода, сгенерированного из Kern.
package kern.runtime

/** result<T, E>: либо значение, либо ошибка. */
class KResult<T, E> private constructor(val isOk: Boolean, private val v: Any?, private val e: Any?) {
    @Suppress("UNCHECKED_CAST")
    val value: T get() = if (isOk) v as T else throw IllegalStateException("result содержит ошибку")
    @Suppress("UNCHECKED_CAST")
    val error: E get() = if (!isOk) e as E else throw IllegalStateException("result содержит значение")
    override fun toString(): String = if (isOk) "ok($v)" else "err($e)"

    companion object {
        fun <T, E> ok(value: T): KResult<T, E> = KResult(true, value, null)
        fun <T, E> err(error: E): KResult<T, E> = KResult(false, null, error)
    }
}

fun kernRange(start: Long, end: Long, inclusive: Boolean, step: Long = 1): Sequence<Long> = sequence {
    if (step == 0L) return@sequence
    var i = start
    if (step > 0) { while (if (inclusive) i <= end else i < end) { yield(i); i += step } }
    else { while (if (inclusive) i >= end else i > end) { yield(i); i += step } }
}

fun kernLen(x: Any?): Int = when (x) {
    is String -> x.length
    is Collection<*> -> x.size
    is Array<*> -> x.size
    else -> throw IllegalArgumentException("нет длины у $x")
}

fun <T> kernSlice(list: List<T>, from: Int, tailCount: Int): MutableList<T> =
    list.drop(from).dropLast(tailCount).toMutableList()
""";
}
