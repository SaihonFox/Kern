using Kern.Binding.Attributes;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>Члены диапазона <c>a..b</c>, <c>a..=b</c>, <c>a..b:step</c>.</summary>
[KernType("range")]
public static class RangeApi
{
	/// <summary>Элементы диапазона целых чисел (для не-целых границ — панику).</summary>
	internal static IEnumerable<long> Values(KRange r)
	{
		if (!r.Start.IsInteger || !r.End.IsInteger) throw new KernPanic("перебор поддерживается только для целочисленных диапазонов");
		long step = r.HasStep ? r.Step.Bits : 1;
		if (step == 0) throw new KernPanic("шаг диапазона не может быть нулевым");
		long end = r.End.Bits;
		if (step > 0) for (long i = r.Start.Bits; r.Inclusive ? i <= end : i < end; i += step) yield return i;
		else for (long i = r.Start.Bits; r.Inclusive ? i >= end : i > end; i += step) yield return i;
	}

	[KernProperty] public static KValue Start(KRange self) => self.Start;
	[KernProperty] public static KValue End(KRange self) => self.End;
	[KernProperty] public static bool IsInclusive(KRange self) => self.Inclusive;
	[KernProperty] public static int Len(KRange self) => Values(self).Count();
	[KernProperty] public static bool IsEmpty(KRange self) => !Values(self).Any();

	[KernMember] public static bool Contains(KRange self, KValue value) => self.Contains(value);

	[KernMember] public static Kern.Binding.Wrappers.KList<long> ToList(KRange self) =>
		Kv.List<long>(TypeFactory.Prim(Kern.Bytecode.PrimKind.I64), Values(self).Select(KValue.I64));
}
