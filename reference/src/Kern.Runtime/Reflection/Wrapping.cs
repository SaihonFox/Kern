using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Values;

namespace Kern.Runtime.Reflection;

/// <summary>Помощники упаковки результатов рефлексии в списки Kern.</summary>
internal static class Wrapping
{
	public static KList<T> Wrap<T>(IEnumerable<object> items) where T : class =>
		Kv.List<T>(items.Select(o => KValue.Obj(o)));
}
