namespace Kern.Lib.Transpile.Targets.CSharp;

/// <summary>Вспомогательный runtime, который кладётся рядом с кодом C#-проекта.</summary>
internal static class CSharpRuntime
{
	public const string Text = """
// Сгенерировано транспилятором Kern → C#. Не редактируйте вручную.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KernGenerated;

/// <summary>Результат Kern: Ok(value) или Err(error).</summary>
public readonly struct KResult<T, E>
{
	private readonly T? _value;
	private readonly E? _error;

	private KResult(bool isOk, T? value, E? error)
	{
		IsOk = isOk;
		_value = value;
		_error = error;
	}

	public bool IsOk { get; }

	public bool IsErr => !IsOk;

	public T Value => IsOk
		? _value!
		: throw new InvalidOperationException("KResult: значение доступно только у Ok");

	public E Error => !IsOk
		? _error!
		: throw new InvalidOperationException("KResult: ошибка доступна только у Err");

	public static KResult<T, E> Ok(T value) => new(true, value, default);

	public static KResult<T, E> Err(E error) => new(false, default, error);

	public override string ToString() => IsOk ? $"Ok({_value})" : $"Err({_error})";
}

public static class KernRuntime
{
	public static int Len<T>(ICollection<T> items) => items.Count;

	public static int Len(string text) => text.Length;

	public static List<T> Slice<T>(IReadOnlyList<T> items, int start, int end, bool inclusive)
	{
		int from = Math.Clamp(start, 0, items.Count);
		int to = Math.Clamp(inclusive ? end + 1 : end, from, items.Count);
		return items.Skip(from).Take(to - from).ToList();
	}

	public static string Slice(string text, int start, int end, bool inclusive)
	{
		int from = Math.Clamp(start, 0, text.Length);
		int to = Math.Clamp(inclusive ? end + 1 : end, from, text.Length);
		return text.Substring(from, to - from);
	}

	public static bool In<T>(T item, IEnumerable<T> items) => items.Contains(item);

	public static bool In<K, V>(K item, IDictionary<K, V> map) => map.ContainsKey(item);

	public static int Pow(int left, int right) => (int)Math.Pow(left, right);

	public static long Pow(long left, long right) => (long)Math.Pow(left, right);

	public static double Pow(double left, double right) => Math.Pow(left, right);

	public static string Format(object? value) =>
		Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

	public static string Format(object? value, string spec) =>
		Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

	public static int Compare<T>(T left, T right) => Comparer<T>.Default.Compare(left!, right!);

	public static ulong Hash(object? value) => (ulong)(value?.GetHashCode() ?? 0);

	public static T Clone<T>(T value) => value;

	public static Type TypeOf(object? value) => value?.GetType() ?? typeof(object);
}
""";
}
