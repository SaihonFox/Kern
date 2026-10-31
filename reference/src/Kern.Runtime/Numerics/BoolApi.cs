using Kern.Binding.Attributes;

namespace Kern.Runtime.Numerics;

/// <summary>Члены встроенного типа bool.</summary>
[KernType("bool")]
public static class BoolApi
{
	[KernMember] public static string ToStr(bool self) => self ? "true" : "false";
	[KernMember] public static int ToI32(bool self) => self ? 1 : 0;
	[KernMember] public static bool Xor(bool self, bool other) => self ^ other;
	[KernProperty] public static bool Negated(bool self) => !self;
}
