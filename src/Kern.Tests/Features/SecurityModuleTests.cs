using Kern.Runtime.Security;
using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// <c>kern.security</c>: SHA-3/HMAC, AES-GCM и ChaCha20, JWT HS256, RSA-подписи и
/// <c>secret&lt;str&gt;</c> (маска в <c>to_str</c>, явный <c>reveal</c>).
/// </summary>
[Collection("timing")]
public sealed class SecurityModuleTests
{
	[Fact]
	public void Hashes_and_hmac_are_hex()
	{
		Assert.Equal(
			"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
			Kern.Runtime.Crypto.CryptoApi.Sha256("abc"));
		Assert.Equal(64, SecurityApi.Sha3_256("abc").Length);
		Assert.Equal(
			"3a985da74fe225b2045c172d6bd390bd855f086e3e9d525b46bfe24511431532",
			SecurityApi.Sha3_256("abc"));
		Assert.Equal(
			"b751850b1a57168a5693cd924b6b096e08f621827444f70d884f5d0240d2712e" +
			"10e116e9192af3c91a7ec57647e3934057340b4cf408d5a56592f8274eec53f0",
			SecurityApi.Sha3_512("abc"));
		Assert.Equal(128, SecurityApi.HmacSha512("key", "abc").Length);
	}

	[Fact]
	public void Blake2b_matches_rfc_vectors_and_supports_keys()
	{
		Assert.Equal(
			"ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d1" +
			"7d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923",
			SecurityApi.Blake2b("abc"));
		Assert.Equal(
			"bddd813c634239723171ef3fee98579b94964e3bb1cb3e427262c8c068d52319",
			SecurityApi.Blake2b("abc", 32));
		Assert.Equal(
			"786a02f742015903c6c6fd852552d272912f4740e15847618a86e217f71f5419" +
			"d25e1031afee585313896444934eb04b903a685b1448b755d56f701afe9be2ce",
			SecurityApi.Blake2b(""));
		string keyed = SecurityApi.Blake2bKeyed("key", "abc");
		Assert.Equal(128, keyed.Length);
		Assert.NotEqual(SecurityApi.Blake2b("abc"), keyed);
	}

	[Fact]
	public void Aes_gcm_round_trips_and_rejects_a_wrong_key()
	{
		var encrypted = SecurityApi.AesGcmEncrypt("0123456789abcdef", "секрет");

		Assert.True(encrypted.IsOk);
		string blob = encrypted.Ok;
		Assert.True(SecurityApi.AesGcmDecrypt("0123456789abcdef", blob).IsOk);
		Assert.False(SecurityApi.AesGcmDecrypt("fedcba9876543210", blob).IsOk);
	}

	[Fact]
	public void Chacha20_round_trips()
	{
		string key = new('k', 32);
		var encrypted = SecurityApi.Chacha20Encrypt(key, "text");
		Assert.True(encrypted.IsOk);
		var decrypted = SecurityApi.Chacha20Decrypt(key, encrypted.Ok);
		Assert.True(decrypted.IsOk);
		Assert.Equal("text", decrypted.Ok);
	}

	[Fact]
	public void Jwt_hs256_round_trips_and_rejects_a_broken_signature()
	{
		string token = SecurityApi.JwtSign("""{"sub":"1"}""", "secret");
		var verified = SecurityApi.JwtVerify(token, "secret");
		Assert.True(verified.IsOk);
		Assert.Equal("""{"sub":"1"}""", verified.Ok);
		Assert.False(SecurityApi.JwtVerify(token[..^1] + "x", "secret").IsOk);
	}

	[Fact]
	public void Secret_type_masks_to_str_and_reveals_explicitly()
	{
		const string source = """
			fun masked() -> str secret<str>("token").to_str()
			fun revealed() -> str secret<str>("token").reveal()
			fun length() -> i32 secret<str>("token").len
			""";

		Assert.Equal(KValue.Str("secret(***)"), StdPipeline.Run(source, "masked"));
		Assert.Equal(KValue.Str("token"), StdPipeline.Run(source, "revealed"));
		Assert.Equal(KValue.I32(5), StdPipeline.Run(source, "length"));
	}

	[Fact]
	public void Rsa_signature_verifies_with_the_public_key()
	{
		string privatePem = SecurityApi.RsaGenerate(2048);
		string publicPem = SecurityApi.RsaPublic(privatePem);
		var signature = SecurityApi.RsaSign(privatePem, "payload");
		Assert.True(signature.IsOk);
		Assert.True(SecurityApi.RsaVerify(publicPem, "payload", signature.Ok));
		Assert.False(SecurityApi.RsaVerify(publicPem, "other", signature.Ok));
	}
}
