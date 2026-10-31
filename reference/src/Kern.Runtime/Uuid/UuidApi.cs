using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;

namespace Kern.Runtime.Uuid;

/// <summary>Идентификаторы: <c>uuid.new()</c> → "3f2b…" (UUID v4), <c>uuid.is_valid(s)</c>.</summary>
[KernType("uuid")]
public static class UuidApi
{
	[KernStatic("new")] public static string New() => Guid.NewGuid().ToString();
	[KernStatic] public static string NewCompact() => Guid.NewGuid().ToString("N");
	[KernStatic] public static bool IsValid(string text) => Guid.TryParse(text, out _);
	[KernStatic] public static string Empty() => Guid.Empty.ToString();
}
