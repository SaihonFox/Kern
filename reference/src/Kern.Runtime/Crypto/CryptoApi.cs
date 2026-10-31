using System.Security.Cryptography;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;

namespace Kern.Runtime.Crypto;

/// <summary>Хэши, HMAC, base64, случайные байты. Результаты хэшей — hex-строки в нижнем регистре.</summary>
[KernType("crypto")]
public static class CryptoApi
{
	private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
	private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

	[KernStatic] public static string Sha256(string text) => Hex(SHA256.HashData(Utf8(text)));
	[KernStatic] public static string Sha512(string text) => Hex(SHA512.HashData(Utf8(text)));
	[KernStatic] public static string Sha1(string text) => Hex(SHA1.HashData(Utf8(text)));
	[KernStatic] public static string Md5(string text) => Hex(MD5.HashData(Utf8(text)));

	[KernStatic] public static string HmacSha256(string key, string text) => Hex(HMACSHA256.HashData(Utf8(key), Utf8(text)));

	[KernStatic] public static string Base64Encode(string text) => Convert.ToBase64String(Utf8(text));

	[KernStatic] public static KResult<string, string> Base64Decode(string text)
	{
		try { return Kv.Ok<string>(Kern.Vm.Values.KValue.Str(Encoding.UTF8.GetString(Convert.FromBase64String(text)))); }
		catch (FormatException) { return Kv.Err<string>("неверный base64"); }
	}

	[KernStatic] public static string HexEncode(string text) => Hex(Utf8(text));

	/// <summary>n случайных байт в hex (криптостойкий генератор).</summary>
	[KernStatic] public static string RandomHex(int n) => Hex(RandomNumberGenerator.GetBytes(System.Math.Max(0, n)));

	/// <summary>Хэш пароля PBKDF2-SHA256: "соль:хэш" (hex), 100 000 итераций.</summary>
	[KernStatic] public static string HashPassword(string password)
	{
		var salt = RandomNumberGenerator.GetBytes(16);
		return Hex(salt) + ":" + Hex(Rfc2898DeriveBytes.Pbkdf2(Utf8(password), salt, 100_000, HashAlgorithmName.SHA256, 32));
	}

	[KernStatic] public static bool VerifyPassword(string password, string stored)
	{
		var parts = stored.Split(':');
		if (parts.Length != 2) return false;
		try
		{
			var salt = Convert.FromHexString(parts[0]);
			var expected = Convert.FromHexString(parts[1]);
			return CryptographicOperations.FixedTimeEquals(
				Rfc2898DeriveBytes.Pbkdf2(Utf8(password), salt, 100_000, HashAlgorithmName.SHA256, expected.Length), expected);
		}
		catch (FormatException) { return false; }
	}
}
