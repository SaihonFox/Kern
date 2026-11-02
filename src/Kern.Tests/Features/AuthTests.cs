using Kern.Runtime.Auth;
using Kern.Tests.Features.Support;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// <c>kern.auth</c> (spec §16, раунд 27): HOTP/TOTP по RFC 4226/6238, секреты и токены.
/// </summary>
public sealed class AuthTests
{
	private const string RfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

	[Theory]
	[InlineData(0, "755224")]
	[InlineData(1, "287082")]
	[InlineData(2, "359152")]
	[InlineData(3, "969429")]
	[InlineData(4, "338314")]
	[InlineData(5, "254676")]
	[InlineData(6, "287922")]
	[InlineData(7, "162583")]
	[InlineData(8, "399871")]
	[InlineData(9, "520489")]
	public void Hotp_matches_rfc4226_vectors(long counter, string expected) =>
		Assert.Equal(expected, AuthApi.Hotp(RfcSecret, counter));

	[Theory]
	[InlineData(59, "287082")]
	[InlineData(1111111109, "081804")]
	[InlineData(1111111111, "050471")]
	[InlineData(1234567890, "005924")]
	[InlineData(2000000000, "279037")]
	[InlineData(20000000000, "353130")]
	public void Totp_matches_rfc6238_vectors(long unixTime, string expected) =>
		Assert.Equal(expected, AuthApi.Totp(RfcSecret, unixTime));

	[Fact]
	public void Totp_round_trips_at_current_time()
	{
		string code = AuthApi.Totp(RfcSecret);

		Assert.Equal(6, code.Length);
		Assert.True(AuthApi.TotpVerify(RfcSecret, code));
		Assert.True(AuthApi.TotpVerify(RfcSecret, code, 0));
	}

	[Fact]
	public void Totp_window_accepts_the_previous_period()
	{
		long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		string previous = AuthApi.Totp(RfcSecret, now - 30);

		Assert.True(AuthApi.TotpVerify(RfcSecret, previous));
		Assert.False(AuthApi.TotpVerify(RfcSecret, previous, 0));
	}

	[Fact]
	public void Tampered_codes_are_false()
	{
		string code = AuthApi.Totp(RfcSecret);
		string tampered = code[..5] + (code[5] == '0' ? '1' : '0');

		Assert.False(AuthApi.TotpVerify(RfcSecret, tampered));
		Assert.False(AuthApi.HotpVerify(RfcSecret, tampered, 0));
		Assert.False(AuthApi.TotpVerify(RfcSecret, "00000"));
	}

	[Fact]
	public void Hotp_window_accepts_neighbour_counters()
	{
		string code = AuthApi.Hotp(RfcSecret, 6);

		Assert.True(AuthApi.HotpVerify(RfcSecret, code, 5, 1));
		Assert.False(AuthApi.HotpVerify(RfcSecret, code, 5, 0));
		Assert.True(AuthApi.HotpVerify(RfcSecret, code, 7, 1));
	}

	[Fact]
	public void Totp_url_is_uri_escaped()
	{
		string url = AuthApi.TotpUrl("ACME Co", "аня@example.com", RfcSecret);

		Assert.StartsWith("otpauth://totp/ACME%20Co:", url);
		Assert.Contains("?secret=" + RfcSecret, url);
		Assert.Contains("&issuer=ACME%20Co", url);
		Assert.Contains("&algorithm=SHA1&digits=6&period=30", url);
		string custom = AuthApi.TotpUrl("ACME", "anna", RfcSecret, 8, 60);
		Assert.Contains("&digits=8&period=60", custom);
	}

	[Fact]
	public void Invalid_or_short_secrets_panic_in_russian()
	{
		Assert.Contains("Base32", Assert.Throws<KernPanic>(() => AuthApi.Totp("ABC1")).Message);
		Assert.Contains("секрет", Assert.Throws<KernPanic>(() => AuthApi.Totp("")).Message);
		Assert.Contains("короткий", Assert.Throws<KernPanic>(() => AuthApi.Hotp("MZXW6", 0)).Message);
	}

	[Fact]
	public void Secrets_are_base32_without_padding()
	{
		string secret = AuthApi.Secret();
		string shorter = AuthApi.Secret(16);

		Assert.Equal(52, secret.Length);
		Assert.Equal(26, shorter.Length);
		Assert.Matches("^[A-Z2-7]+$", secret);
		Assert.NotEqual(secret, AuthApi.Secret());
		Assert.NotEqual(secret, shorter);
	}

	[Fact]
	public void Tokens_use_url_safe_alphabet_and_expected_length()
	{
		string token = AuthApi.Token();
		string smaller = AuthApi.Token(8);
		string hex = AuthApi.TokenHex();
		string hex4 = AuthApi.TokenHex(4);

		Assert.Equal(43, token.Length);
		Assert.Equal(11, smaller.Length);
		Assert.Matches("^[A-Za-z0-9_-]+$", token);
		Assert.DoesNotContain('=', token);
		Assert.Equal(32, hex.Length);
		Assert.Matches("^[0-9a-f]+$", hex);
		Assert.Equal(8, hex4.Length);
	}

	[Fact]
	public void Constant_time_eq_compares_whole_strings()
	{
		Assert.True(AuthApi.ConstantTimeEq("секрет", "секрет"));
		Assert.False(AuthApi.ConstantTimeEq("секрет", "секрет2"));
		Assert.False(AuthApi.ConstantTimeEq("abc", "abd"));
		Assert.False(AuthApi.ConstantTimeEq("", "a"));
	}

	[Fact]
	public void Kern_module_is_wired()
	{
		const string source = """
			use kern.auth[auth]

			fun run() -> str {
				var secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"
				var code = auth.hotp(secret, 0)
				var ok = auth.hotp_verify(secret, code, 0)
				var token = auth.token(8)
				var full_token = auth.token()
				var fresh = auth.secret(16)
				var full_secret = auth.secret()
				var totp = auth.totp(secret)
				var totp_ok = auth.totp_verify(secret, totp)
				var url = auth.totp_url("Kern", "a", secret)
				var url_ok = url.starts_with("otpauth://totp/")
				var hex = auth.token_hex(4)
				var full_hex = auth.token_hex()
				var eq = auth.constant_time_eq(secret, secret)
				f"${code}/${ok}/${token.len}/${full_token.len}/${fresh.len}/${full_secret.len}/" +
					f"${totp.len}/${totp_ok}/${url_ok}/${hex.len}/${full_hex.len}/${eq}"
			}
			""";

		Assert.Equal(
			KValue.Str("755224/true/11/43/26/52/6/true/true/8/32/true"),
			StdPipeline.Run(source, "run"));
	}
}
