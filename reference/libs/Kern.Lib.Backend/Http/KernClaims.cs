using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;

namespace Kern.Lib.Backend.Http;

/// <summary>Содержимое проверенного JWT.</summary>
[KernType("claims")]
public sealed class KernClaims(string subject, List<string> roles, long expires)
{
	[KernProperty] public string Sub { get; } = subject;
	[KernProperty] public KList<string> Roles => Kv.Strs(roles);
	/// <summary>Unix-время истечения (секунды).</summary>
	[KernProperty] public long Exp { get; } = expires;
	internal List<string> RoleList => roles;
}
