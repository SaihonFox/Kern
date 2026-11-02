using System.Security.Cryptography;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Security;

/// <summary>
/// <c>kern.security</c>: прикладная безопасность поверх .NET — хеши SHA-2/SHA-3, HMAC,
/// AES-GCM и ChaCha20-Poly1305, RSA-подписи, JWT HS256, TOTP, PBKDF2, сравнение за
/// постоянное время и криптостойкие случайные байты. Ключи и данные — UTF-8 строки;
/// бинарные значения упаковываются в Base64.
/// </summary>
[KernModule("kern.security")]
public static class SecurityApi
{
	private const int MaxRandomBytes = 1 << 20;
	private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

	/// <summary><c>sha3_256(text)</c>: hex SHA3-256.</summary>
	[KernMember]
	[KernName("sha3_256")]
	public static string Sha3_256(string text) => Hex(Sha3Hash.Sha3_256(Utf8.GetBytes(text)));

	/// <summary><c>sha3_512(text)</c>: hex SHA3-512.</summary>
	[KernMember]
	[KernName("sha3_512")]
	public static string Sha3_512(string text) => Hex(Sha3Hash.Sha3_512(Utf8.GetBytes(text)));

	/// <summary><c>hmac_sha512(key, text)</c>: hex HMAC-SHA-512.</summary>
	[KernMember]
	public static string HmacSha512(string key, string text) =>
		Hex(HMACSHA512.HashData(Utf8.GetBytes(key), Utf8.GetBytes(text)));

	/// <summary><c>random_bytes(n)</c>: <c>list&lt;u8&gt;</c> из криптостойкого генератора.</summary>
	[KernMember]
	[KernReturn("list<u8>")]
	public static KValue RandomBytes(Interpreter interpreter, int count)
	{
		if (count < 0 || count > MaxRandomBytes)
			throw new KernPanic($"security.random_bytes: число байтов должно быть 0..{MaxRandomBytes}");
		byte[] bytes = RandomNumberGenerator.GetBytes(count);
		return KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.U8), bytes.Select(KValue.U8)));
	}

	/// <summary><c>hex_encode(text)</c>: UTF-8 текст в hex.</summary>
	[KernMember]
	public static string HexEncode(string text) => Hex(Utf8.GetBytes(text));

	/// <summary><c>hex_decode(hex)</c>: hex в UTF-8 текст или <c>Err</c>.</summary>
	[KernMember]
	public static KResult<string, string> HexDecode(string text)
	{
		try
		{
			return KResult<string, string>.Success(Utf8.GetString(Convert.FromHexString(text)));
		}
		catch (FormatException)
		{
			return KResult<string, string>.Failure("security.hex_decode: неверный hex");
		}
	}

	/// <summary>
	/// <c>aes_gcm_encrypt(key, text)</c>: AES-GCM с 12-байтовым nonce и 16-байтовым тегом;
	/// результат — Base64(nonce || tag || ciphertext). Ключ — 16/24/32 байта UTF-8.
	/// </summary>
	[KernMember]
	public static KResult<string, string> AesGcmEncrypt(string key, string text)
	{
		byte[] keyBytes = KeyBytes("security.aes_gcm_encrypt", key);
		byte[] nonce = RandomNumberGenerator.GetBytes(12);
		byte[] plain = Utf8.GetBytes(text);
		byte[] cipher = new byte[plain.Length];
		byte[] tag = new byte[16];
		using var aes = new AesGcm(keyBytes, tag.Length);
		aes.Encrypt(nonce, plain, cipher, tag);
		return KResult<string, string>.Success(Convert.ToBase64String([.. nonce, .. tag, .. cipher]));
	}

	/// <summary><c>aes_gcm_decrypt(key, blob)</c>: расшифровать результат <c>aes_gcm_encrypt</c>.</summary>
	[KernMember]
	public static KResult<string, string> AesGcmDecrypt(string key, string blob)
	{
		try
		{
			byte[] keyBytes = KeyBytes("security.aes_gcm_decrypt", key);
			byte[] data = Convert.FromBase64String(blob);
			if (data.Length < 28)
				return KResult<string, string>.Failure("security.aes_gcm_decrypt: данные короче nonce+tag");
			byte[] nonce = data[..12];
			byte[] tag = data[12..28];
			byte[] cipher = data[28..];
			byte[] plain = new byte[cipher.Length];
			using var aes = new AesGcm(keyBytes, tag.Length);
			aes.Decrypt(nonce, cipher, tag, plain);
			return KResult<string, string>.Success(Utf8.GetString(plain));
		}
		catch (CryptographicException)
		{
			return KResult<string, string>.Failure("security.aes_gcm_decrypt: неверный ключ или повреждённые данные");
		}
		catch (FormatException)
		{
			return KResult<string, string>.Failure("security.aes_gcm_decrypt: неверный Base64");
		}
	}

	/// <summary><c>chacha20_encrypt(key, text)</c>: ChaCha20-Poly1305, ключ 32 байта UTF-8.</summary>
	[KernMember]
	public static KResult<string, string> Chacha20Encrypt(string key, string text)
	{
		byte[] keyBytes = KeyBytes("security.chacha20_encrypt", key, required: 32);
		byte[] nonce = RandomNumberGenerator.GetBytes(12);
		byte[] plain = Utf8.GetBytes(text);
		byte[] cipher = new byte[plain.Length];
		byte[] tag = new byte[16];
		using var chacha = new ChaCha20Poly1305(keyBytes);
		chacha.Encrypt(nonce, plain, cipher, tag);
		return KResult<string, string>.Success(Convert.ToBase64String([.. nonce, .. tag, .. cipher]));
	}

	/// <summary><c>chacha20_decrypt(key, blob)</c>: расшифровать результат <c>chacha20_encrypt</c>.</summary>
	[KernMember]
	public static KResult<string, string> Chacha20Decrypt(string key, string blob)
	{
		try
		{
			byte[] keyBytes = KeyBytes("security.chacha20_decrypt", key, required: 32);
			byte[] data = Convert.FromBase64String(blob);
			if (data.Length < 28)
				return KResult<string, string>.Failure("security.chacha20_decrypt: данные короче nonce+tag");
			byte[] plain = new byte[data.Length - 28];
			using var chacha = new ChaCha20Poly1305(keyBytes);
			chacha.Decrypt(data[..12], data[28..], data[12..28], plain);
			return KResult<string, string>.Success(Utf8.GetString(plain));
		}
		catch (CryptographicException)
		{
			return KResult<string, string>.Failure("security.chacha20_decrypt: неверный ключ или повреждённые данные");
		}
		catch (FormatException)
		{
			return KResult<string, string>.Failure("security.chacha20_decrypt: неверный Base64");
		}
	}

	/// <summary><c>rsa_generate(bits)</c>: PKCS#8 private PEM (2048..4096 бит).</summary>
	[KernMember]
	public static string RsaGenerate(int bits)
	{
		if (bits < 2048 || bits > 4096)
			throw new KernPanic("security.rsa_generate: размер ключа должен быть 2048..4096 бит");
		using RSA rsa = RSA.Create(bits);
		return rsa.ExportPkcs8PrivateKeyPem();
	}

	/// <summary><c>rsa_public(private_pem)</c>: SubjectPublicKeyInfo PEM из private PEM.</summary>
	[KernMember]
	public static string RsaPublic(string privatePem)
	{
		using RSA rsa = RSA.Create();
		rsa.ImportFromPem(privatePem);
		return rsa.ExportSubjectPublicKeyInfoPem();
	}

	/// <summary><c>rsa_sign(private_pem, text)</c>: Base64-подпись SHA-256/PKCS#1.</summary>
	[KernMember]
	public static KResult<string, string> RsaSign(string privatePem, string text)
	{
		try
		{
			using RSA rsa = RSA.Create();
			rsa.ImportFromPem(privatePem);
			return KResult<string, string>.Success(Convert.ToBase64String(
				rsa.SignData(Utf8.GetBytes(text), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
		}
		catch (CryptographicException)
		{
			return KResult<string, string>.Failure("security.rsa_sign: неверный private-ключ");
		}
	}

	/// <summary><c>rsa_verify(public_pem, text, signature)</c>: проверить Base64-подпись.</summary>
	[KernMember]
	public static bool RsaVerify(string publicPem, string text, string signature)
	{
		try
		{
			using RSA rsa = RSA.Create();
			rsa.ImportFromPem(publicPem);
			return rsa.VerifyData(
				Utf8.GetBytes(text), Convert.FromBase64String(signature),
				HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		}
		catch (CryptographicException)
		{
			return false;
		}
		catch (FormatException)
		{
			return false;
		}
	}

	/// <summary><c>jwt_sign(payload_json, secret)</c>: JWT HS256 (header/base64url/payload/подпись).</summary>
	[KernMember]
	public static string JwtSign(string payloadJson, string secret)
	{
		string header = Base64Url(Utf8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
		string payload = Base64Url(Utf8.GetBytes(payloadJson));
		string signingInput = header + "." + payload;
		string signature = Base64Url(HMACSHA256.HashData(Utf8.GetBytes(secret), Utf8.GetBytes(signingInput)));
		return signingInput + "." + signature;
	}

	/// <summary><c>jwt_verify(token, secret)</c>: payload JSON или <c>Err</c>.</summary>
	[KernMember]
	public static KResult<string, string> JwtVerify(string token, string secret)
	{
		string[] parts = token.Split('.');
		if (parts.Length != 3)
			return KResult<string, string>.Failure("security.jwt_verify: ожидался токен из трёх частей");
		try
		{
			byte[] expected = HMACSHA256.HashData(Utf8.GetBytes(secret), Utf8.GetBytes(parts[0] + "." + parts[1]));
			if (!CryptographicOperations.FixedTimeEquals(expected, FromBase64Url(parts[2])))
				return KResult<string, string>.Failure("security.jwt_verify: неверная подпись");
			return KResult<string, string>.Success(Utf8.GetString(FromBase64Url(parts[1])));
		}
		catch (FormatException)
		{
			return KResult<string, string>.Failure("security.jwt_verify: неверный Base64URL");
		}
	}

	private static byte[] KeyBytes(string name, string key, int? required = null)
	{
		byte[] bytes = Utf8.GetBytes(key);
		if (bytes.Length is not (16 or 24 or 32) || (required is { } size && bytes.Length != size))
			throw new KernPanic($"{name}: ключ должен быть {(required is { } exact ? $"{exact}" : "16, 24 или 32")} байта UTF-8");
		return bytes;
	}

	private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

	private static string Base64Url(byte[] bytes) =>
		Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	private static byte[] FromBase64Url(string text)
	{
		string padded = text.Replace('-', '+').Replace('_', '/');
		return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
	}
}

/// <summary>Значение <c>secret&lt;str&gt;</c>: не печатается и не сериализуется открыто.</summary>
[KernType("secret", GenericArity = 1)]
public sealed class KernSecret
{
	public KernSecret(string value) => Value = value;

	public string Value { get; }
}

/// <summary>Фабрика и члены <c>secret&lt;str&gt;</c> (spec §16): <c>reveal()</c> и маскирующий <c>to_str()</c>.</summary>
[KernType("secret", GenericArity = 1)]
public static class SecretApi
{
	/// <summary><c>secret(text)</c>: обернуть секрет; в логах и <c>to_str</c> он виден как <c>secret(***)</c>.</summary>
	[KernCtor]
	[KernName("secret")]
	[KernReturn("secret<str>")]
	public static KValue Create(string value) => KValue.Obj(new KNativeBox(new KernSecret(value)));

	/// <summary><c>s.reveal()</c>: настоящее значение (осознанное раскрытие).</summary>
	[KernMember]
	public static string Reveal(KNativeBox self) => Require(self).Value;

	/// <summary><c>s.to_str()</c>: маска, не раскрывающая секрет.</summary>
	[KernMember]
	[KernName("to_str")]
	public static string ToStr(KNativeBox self) => "secret(***)";

	/// <summary><c>s.len</c>: длина секрета (без раскрытия содержимого).</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Value.Length;

	private static KernSecret Require(KNativeBox self) => self.Value switch
	{
		KernSecret secret => secret,
		_ => throw new KernPanic($"ожидался secret<str>, получено {self.Value.GetType().Name}"),
	};
}
