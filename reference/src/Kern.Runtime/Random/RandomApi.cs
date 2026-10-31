using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Execution;

namespace Kern.Runtime.Random;

/// <summary>Генератор случайных чисел: <c>random.next(1, 7)</c> (верхняя граница не включается).</summary>
[KernType("random")]
public static class RandomApi
{
	private static System.Random Rng => System.Random.Shared;

	[KernStatic] public static int Next(int min, int max) =>
		min < max ? Rng.Next(min, max) : throw new KernPanic($"random.next: min ({min}) должен быть меньше max ({max})");
	[KernStatic] public static long NextI64(long min, long max) =>
		min < max ? Rng.NextInt64(min, max) : throw new KernPanic($"random.next_i64: min ({min}) должен быть меньше max ({max})");
	[KernStatic] public static double Float() => Rng.NextDouble();
	[KernStatic] public static bool Bool() => Rng.Next(2) == 1;
	[KernStatic] public static string Hex(int length) =>
		string.Concat(Enumerable.Range(0, System.Math.Max(0, length)).Select(_ => "0123456789abcdef"[Rng.Next(16)]));

	[KernStatic] public static T Choice<T>(KList<T> items) =>
		items.Count > 0 ? Kv.As<T>(items[Rng.Next(items.Count)]) : throw new KernPanic("random.choice: список пуст");
}
