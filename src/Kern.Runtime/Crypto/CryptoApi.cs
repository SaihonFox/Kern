using System.Security.Cryptography;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Vm.Execution;

namespace Kern.Runtime.Crypto;

/// <summary>
/// <c>kern.crypto</c> (spec §16, раунд 5): хеши, HMAC, Base64, случайный hex и пароли (PBKDF2).
/// Хеши возвращаются строчным hex; ошибки декодирования — <c>Err</c>.
/// </summary>
[KernModule("kern.crypto")]
public static class CryptoApi
{
	private const int PasswordIterations = 100_000;
	private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

	/// <summary><c>crypto.md5(text)</c>: hex MD5 (совместимость; для паролей не использовать).</summary>
	[KernMember]
	public static string Md5(string text) => Hex(MD5.HashData(Utf8.GetBytes(text)));

	/// <summary><c>crypto.sha1(text)</c>: hex SHA-1.</summary>
	[KernMember]
	public static string Sha1(string text) => Hex(SHA1.HashData(Utf8.GetBytes(text)));

	/// <summary><c>crypto.sha256(text)</c>: hex SHA-256.</summary>
	[KernMember]
	public static string Sha256(string text) => Hex(SHA256.HashData(Utf8.GetBytes(text)));

	/// <summary><c>crypto.sha512(text)</c>: hex SHA-512.</summary>
	[KernMember]
	public static string Sha512(string text) => Hex(SHA512.HashData(Utf8.GetBytes(text)));

	/// <summary><c>crypto.hmac_sha256(key, text)</c>: hex HMAC-SHA-256.</summary>
	[KernMember]
	public static string HmacSha256(string key, string text) =>
		Hex(HMACSHA256.HashData(Utf8.GetBytes(key), Utf8.GetBytes(text)));

	/// <summary><c>crypto.base64_encode(text)</c>: Base64 UTF-8.</summary>
	[KernMember]
	public static string Base64Encode(string text) => Convert.ToBase64String(Utf8.GetBytes(text));

	/// <summary><c>crypto.base64_decode(text)</c>: обратное преобразование.</summary>
	[KernMember]
	public static KResult<string, string> Base64Decode(string text)
	{
		try
		{
			return KResult<string, string>.Success(Utf8.GetString(Convert.FromBase64String(text)));
		}
		catch (FormatException)
		{
			return KResult<string, string>.Failure("crypto: неверный Base64");
		}
	}

	/// <summary><c>crypto.random_hex(bytes)</c>: hex из криптостойких случайных байтов.</summary>
	[KernMember]
	public static string RandomHex(int bytes)
	{
		if (bytes < 0 || bytes > 4096)
			throw new KernPanic("crypto.random_hex: число байтов должно быть 0..4096");
		return Hex(RandomNumberGenerator.GetBytes(bytes));
	}

	/// <summary>
	/// <c>crypto.hash_password(password)</c>: PBKDF2-SHA256, формат
	/// <c>pbkdf2$итерации$соль$хеш</c> (соль и хеш — Base64).
	/// </summary>
	[KernMember]
	public static string HashPassword(string password)
	{
		byte[] salt = RandomNumberGenerator.GetBytes(16);
		byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
			Utf8.GetBytes(password), salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
		return $"pbkdf2${PasswordIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
	}

	/// <summary><c>crypto.verify_password(password, hash)</c>: проверка формата <c>hash_password</c>.</summary>
	[KernMember]
	public static bool VerifyPassword(string password, string hash)
	{
		string[] parts = hash.Split('$');
		if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out int iterations))
			return false;

		try
		{
			byte[] salt = Convert.FromBase64String(parts[2]);
			byte[] expected = Convert.FromBase64String(parts[3]);
			byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
				Utf8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
			return CryptographicOperations.FixedTimeEquals(actual, expected);
		}
		catch (FormatException)
		{
			return false;
		}
	}

	private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
