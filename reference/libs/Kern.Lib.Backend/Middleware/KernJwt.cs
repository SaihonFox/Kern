using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Lib.Backend.Http;
using Kern.Runtime.Core;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Middleware;

/// <summary>JWT: `jwt.sign("аня", ["admin"], secret, 3600)`, `jwt.verify(token, secret)`.</summary>
[KernType("jwt")]
public static class KernJwt
{
	[KernStatic] public static string Sign(string subject, KList<string> roles, string secret, long ttlSeconds) =>
		Jwt.Create(subject, roles.Items.Select(r => r.AsStr), secret, ttlSeconds);

	[KernStatic]
	public static KResult<KernClaims, string> Verify(string token, string secret)
	{
		var (claims, error) = Jwt.Verify(token, secret);
		return claims is not null ? KResult<KernClaims, string>.Ok(KValue.Obj(claims)) : KResult<KernClaims, string>.Err(KValue.Str(error!));
	}
}
