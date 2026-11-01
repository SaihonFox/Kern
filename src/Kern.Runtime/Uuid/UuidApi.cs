using Kern.Binding.Attributes;
using Kern.Vm.Execution;

namespace Kern.Runtime.Uuid;

/// <summary><c>kern.uuid</c> (spec §16, раунд 5): UUID версии 4.</summary>
[KernModule("kern.uuid")]
public static class UuidApi
{
	/// <summary><c>uuid.new()</c>: новый UUID v4 в нижнем регистре с дефисами (36 символов).</summary>
	[KernMember]
	public static string New() => System.Guid.NewGuid().ToString("D");
}
