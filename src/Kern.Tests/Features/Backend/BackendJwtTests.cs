using Kern.Binding.Values;
using Kern.Lib.Backend.Http;
using Kern.Lib.Backend.Middleware;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features.Backend;

/// <summary>JWT HS256: подпись, проверка, срок и повреждённые токены.</summary>
public sealed class BackendJwtTests
{
	[Fact]
	public void Signed_token_round_trips_claims()
	{
		string token = Jwt.Create("Аня", ["admin", "user"], "secret", 3600);

		(KernClaims? claims, string? error) = Jwt.Verify(token, "secret");

		Assert.Null(error);
		Assert.NotNull(claims);
		Assert.Equal("Аня", claims!.Sub);
		Assert.Equal(new[] { "admin", "user" }, claims.RoleList);
		Assert.True(claims.Exp > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
	}

	[Fact]
	public void Wrong_secret_and_malformed_token_are_errors()
	{
		string token = Jwt.Create("anya", ["admin"], "secret", 3600);

		Assert.Null(Jwt.Verify(token, "other-secret").Claims);
		Assert.Null(Jwt.Verify("not-a-jwt", "secret").Claims);
		Assert.Null(Jwt.Verify("a.b", "secret").Claims);
	}

	[Fact]
	public void Expired_token_is_rejected()
	{
		string token = Jwt.Create("anya", [], "secret", -1);

		(KernClaims? claims, string? error) = Jwt.Verify(token, "secret");

		Assert.Null(claims);
		Assert.Equal("срок истёк", error);
	}

	[Fact]
	public void Api_sign_accepts_a_kern_list()
	{
		var roles = new KList<string>(KValue.Obj(new Kern.Vm.Values.KList(null, [KValue.Str("admin")])));

		string token = JwtApi.Sign("anya", roles, "secret", 60);
		(KernClaims? claims, string? error) = Jwt.Verify(token, "secret");

		Assert.Null(error);
		Assert.Equal(new[] { "admin" }, claims!.RoleList);
	}
}
