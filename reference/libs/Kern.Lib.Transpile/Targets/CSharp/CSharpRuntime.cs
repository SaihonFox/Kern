namespace Kern.Lib.Transpile.Targets.CSharp;

/// <summary>Вспомогательный код, который кладётся рядом с результатом (результат-тип, диапазоны, in, срезы).</summary>
internal static class CSharpRuntime
{
	public const string Text = """
// Вспомогательные типы для кода, сгенерированного из Kern.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace KernGenerated;

/// <summary>`void` в позиции аргумента типа (result&lt;void, E&gt;, Task и т.п.).</summary>
public readonly record struct Unit;

/// <summary>result&lt;T, E&gt;: либо значение, либо ошибка.</summary>
public sealed class KResult<T, E>
{
	private readonly T? _value;
	private readonly E? _error;
	public bool IsOk { get; }
	private KResult(bool ok, T? value, E? error) { IsOk = ok; _value = value; _error = error; }
	public static KResult<T, E> Ok(T value) => new(true, value, default);
	public static KResult<T, E> Err(E error) => new(false, default, error);
	public T Value => IsOk ? _value! : throw new InvalidOperationException("result содержит ошибку");
	public E Error => !IsOk ? _error! : throw new InvalidOperationException("result содержит значение");
	public override string ToString() => IsOk ? $"ok({_value})" : $"err({_error})";
}

public static class KernRuntime
{
	public static IEnumerable<long> Range(long start, long end, bool inclusive, long step = 1)
	{
		if (step == 0) yield break;
		if (step > 0) for (long i = start; inclusive ? i <= end : i < end; i += step) yield return i;
		else for (long i = start; inclusive ? i >= end : i > end; i += step) yield return i;
	}

	public static bool In<T>(T item, IEnumerable<T> items) => items.Contains(item);
	public static bool In<K, V>(K key, IDictionary<K, V> dict) => dict.ContainsKey(key);
	public static bool In(string item, string text) => text.Contains(item);
	public static bool In(char item, string text) => text.Contains(item);

	public static int Len<T>(IReadOnlyList<T> list) => list.Count;
	public static int Len(string s) => s.Length;

	public static List<T> Slice<T>(IReadOnlyList<T> list, int from, int tailCount) =>
		[.. list.Skip(from).Take(Math.Max(0, list.Count - from - tailCount))];
}
""";
}
