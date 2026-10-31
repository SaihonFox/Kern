using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;

namespace Kern.Runtime.Text;

// Разбиение и объединение строк.
public static partial class StrApi
{
	[KernMember] public static KList<string> Split(string self, string separator) =>
		Kv.Strs(separator.Length == 0 ? self.Select(c => c.ToString()) : self.Split(separator));
	[KernMember] public static KList<string> Split(string self, char separator) => Kv.Strs(self.Split(separator));
	[KernMember] public static KList<string> SplitN(string self, string separator, int max) => Kv.Strs(self.Split(separator, max));
	[KernMember] public static KList<string> SplitNonEmpty(string self, string separator) =>
		Kv.Strs(self.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

	[KernProperty] public static KList<string> Lines(string self) =>
		Kv.Strs(self.Split(["\r\n", "\n", "\r"], StringSplitOptions.None));
	[KernProperty] public static KList<string> Words(string self) =>
		Kv.Strs(self.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

	/// <summary>Куски фиксированного размера: "abcde".chunks(2) → ["ab","cd","e"].</summary>
	[KernMember] public static KList<string> Chunks(string self, int size)
	{
		if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size), "размер куска должен быть > 0");
		return Kv.Strs(Enumerable.Range(0, (self.Length + size - 1) / size).Select(i => self.Substring(i * size, System.Math.Min(size, self.Length - i * size))));
	}

	/// <summary>Разбить по первому вхождению разделителя: (до, после); если не найден — (вся строка, "").</summary>
	[KernMember] public static KTuple<string, string> SplitOnce(string self, string sep)
	{
		int i = self.IndexOf(sep, StringComparison.Ordinal);
		var (a, b) = i < 0 ? (self, "") : (self[..i], self[(i + sep.Length)..]);
		return new(Kv.Tuple(Kern.Vm.Values.KValue.Str(a), Kern.Vm.Values.KValue.Str(b)));
	}

	/// <summary>Объединить элементы списка через разделитель (str.join(list)).</summary>
	[KernMember] public static string Join(string self, KList<string> items) =>
		string.Join(self, items.Items.Select(x => x.AsStr));
}
