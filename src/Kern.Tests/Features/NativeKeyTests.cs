using Kern.Vm.Native;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>Диагностика нативных привязок печатается в Kern-виде, а не `owner|name|...`.</summary>
public sealed class NativeKeyTests
{
	[Fact]
	public void Describe_renders_kern_syntax()
	{
		Assert.Equal(
			"mod.f(i32,str)",
			NativeKey.Describe(NativeKey.Create("mod", "f", NativeMemberKind.Method, false, ["i32", "str"])));
		Assert.Equal(
			"mod.value",
			NativeKey.Describe(NativeKey.Create("mod", "value", NativeMemberKind.PropertyGet, true, [])));
		Assert.Equal(
			"mod.value = value",
			NativeKey.Describe(NativeKey.Create("mod", "value", NativeMemberKind.PropertySet, true, ["str"])));
		Assert.Equal(
			"Type(i32)",
			NativeKey.Describe(NativeKey.Create("Type", "ctor", NativeMemberKind.Constructor, false, ["i32"])));
	}

	[Fact]
	public void Describe_keeps_a_corrupted_key_unchanged()
	{
		Assert.Equal("broken", NativeKey.Describe("broken"));
	}
}
