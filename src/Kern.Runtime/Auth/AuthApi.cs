using System.Security.Cryptography;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Vm.Execution;

namespace Kern.Runtime.Auth;

/// <summary>
/// <c>kern.auth</c> (spec §16, backend-пакет): секреты, одноразовые коды TOTP/HOTP
/// (RFC 6238/4226, HMAC-SHA1) и случайные токены.
/// Неверный Base32/короткий секрет — <see cref="KernPanic"/> по-русски; неверный код — <c>false</c>.
/// </summary>
[KernModule("kern.auth")]
public static class AuthApi
{
	private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
	private const int MinimumSecretBytes = 10;
	private const int MaximumRandomBytes = 4096;

	/// <summary><c>auth.secret()</c>: 32 случайных байта в Base32 без паддинга.</summary>
	[KernMember]
	[KernName("secret")]
	public static string Secret() => Secret(32);

	/// <summary><c>auth.secret(nbytes)</c>: случайный секрет в Base32 без паддинга.</summary>
	[KernMember]
	[KernName("secret")]
	public static string Secret(int nbytes) => Base32Encode(RandomBytes(nbytes));

	/// <summary><c>auth.totp(secret)</c>: 6-значный код для текущего времени (период 30 с).</summary>
	[KernMember]
	[KernName("totp")]
	public static string Totp(string secret) =>
		Totp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

	/// <summary><c>auth.totp(secret, unix_time)</c>: 6-значный код на момент времени.</summary>
	[KernMember]
	[KernName("totp")]
	public static string Totp(string secret, long unixTime)
	{
		if (unixTime < 0)
			throw new KernPanic("auth.totp: время не может быть отрицательным");
		byte[] key = SecretBytes(secret);
		return HotpCode(key, unixTime / 30, 6);
	}

	/// <summary>
	/// <c>auth.totp_verify(secret, code)</c>: проверка с окном 1 (предыдущий/следующий период).
	/// Сравнение постоянного времени.
	/// </summary>
	[KernMember]
	[KernName("totp_verify")]
	public static bool TotpVerify(string secret, string code) => TotpVerify(secret, code, 1);

	/// <summary><c>auth.totp_verify(secret, code, window)</c>: проверка с окном счётчиков.</summary>
	[KernMember]
	[KernName("totp_verify")]
	public static bool TotpVerify(string secret, string code, int window)
	{
		if (window < 0)
			throw new KernPanic("auth.totp_verify: окно не может быть отрицательным");
		long counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
		return VerifyCode(SecretBytes(secret), code, counter, window, 6);
	}

	/// <summary><c>auth.totp_url(issuer, account, secret)</c>: otpauth-ссылка (6 цифр, 30 с).</summary>
	[KernMember]
	[KernName("totp_url")]
	public static string TotpUrl(string issuer, string account, string secret) =>
		TotpUrl(issuer, account, secret, 6, 30);

	/// <summary><c>auth.totp_url(issuer, account, secret, digits, period)</c>: otpauth-ссылка.</summary>
	[KernMember]
	[KernName("totp_url")]
	public static string TotpUrl(string issuer, string account, string secret, int digits, int period)
	{
		if (digits < 1 || digits > 10)
			throw new KernPanic("auth.totp_url: digits должен быть 1..10");
		if (period < 1 || period > 3600)
			throw new KernPanic("auth.totp_url: period должен быть 1..3600");
		_ = SecretBytes(secret);
		string label = Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(account);
		return "otpauth://totp/" + label +
			"?secret=" + Uri.EscapeDataString(secret) +
			"&issuer=" + Uri.EscapeDataString(issuer) +
			"&algorithm=SHA1&digits=" + digits +
			"&period=" + period;
	}

	/// <summary><c>auth.hotp(secret, counter)</c>: 6-значный код RFC 4226.</summary>
	[KernMember]
	[KernName("hotp")]
	public static string Hotp(string secret, long counter)
	{
		if (counter < 0)
			throw new KernPanic("auth.hotp: счётчик не может быть отрицательным");
		return HotpCode(SecretBytes(secret), counter, 6);
	}

	/// <summary><c>auth.hotp_verify(secret, code, counter)</c>: проверка без окна.</summary>
	[KernMember]
	[KernName("hotp_verify")]
	public static bool HotpVerify(string secret, string code, long counter) =>
		HotpVerify(secret, code, counter, 0);

	/// <summary><c>auth.hotp_verify(secret, code, counter, window)</c>: проверка с окном.</summary>
	[KernMember]
	[KernName("hotp_verify")]
	public static bool HotpVerify(string secret, string code, long counter, int window)
	{
		if (counter < 0)
			throw new KernPanic("auth.hotp_verify: счётчик не может быть отрицательным");
		if (window < 0)
			throw new KernPanic("auth.hotp_verify: окно не может быть отрицательным");
		return VerifyCode(SecretBytes(secret), code, counter, window, 6);
	}

	/// <summary><c>auth.token()</c>: 32 случайных байта в Base64URL без паддинга.</summary>
	[KernMember]
	[KernName("token")]
	public static string Token() => Token(32);

	/// <summary><c>auth.token(nbytes)</c>: случайный токен в Base64URL без паддинга.</summary>
	[KernMember]
	[KernName("token")]
	public static string Token(int nbytes) =>
		Convert.ToBase64String(RandomBytes(nbytes))
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');

	/// <summary><c>auth.token_hex()</c>: 16 случайных байтов в hex нижнего регистра.</summary>
	[KernMember]
	[KernName("token_hex")]
	public static string TokenHex() => TokenHex(16);

	/// <summary><c>auth.token_hex(nbytes)</c>: случайные байты в hex нижнего регистра.</summary>
	[KernMember]
	[KernName("token_hex")]
	public static string TokenHex(int nbytes) => Convert.ToHexStringLower(RandomBytes(nbytes));

	/// <summary><c>auth.constant_time_eq(a, b)</c>: сравнение строк постоянного времени.</summary>
	[KernMember]
	[KernName("constant_time_eq")]
	public static bool ConstantTimeEq(string a, string b)
	{
		byte[] left = Encoding.UTF8.GetBytes(a ?? "");
		byte[] right = Encoding.UTF8.GetBytes(b ?? "");
		return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
	}

	private static byte[] RandomBytes(int nbytes)
	{
		if (nbytes < 0 || nbytes > MaximumRandomBytes)
			throw new KernPanic($"auth: число байтов должно быть 0..{MaximumRandomBytes}");
		return RandomNumberGenerator.GetBytes(nbytes);
	}

	private static string Base32Encode(byte[] bytes)
	{
		var builder = new StringBuilder((bytes.Length * 8 + 4) / 5);
		var buffer = 0;
		var bits = 0;
		foreach (byte value in bytes)
		{
			buffer = (buffer << 8) | value;
			bits += 8;
			while (bits >= 5)
			{
				bits -= 5;
				builder.Append(Base32Alphabet[(buffer >> bits) & 31]);
			}
		}

		if (bits > 0)
			builder.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
		return builder.ToString();
	}

	private static byte[] DecodeSecret(string secret)
	{
		if (string.IsNullOrWhiteSpace(secret))
			throw new KernPanic("auth: секрет: пустая строка");
		string text = secret.Trim();
		int end = text.Length;
		while (end > 0 && text[end - 1] == '=')
			end--;
		if (text.AsSpan(0, end).IndexOf('=') >= 0)
			throw new KernPanic("auth: секрет: неверный Base32 (паддинг внутри строки)");

		var output = new List<byte>(end * 5 / 8);
		var buffer = 0;
		var bits = 0;
		for (var index = 0; index < end; index++)
		{
			char symbol = text[index];
			int value = Base32Value(symbol);
			if (value < 0)
				throw new KernPanic($"auth: секрет: неверный Base32 (символ '{symbol}')");
			buffer = (buffer << 5) | value;
			bits += 5;
			if (bits >= 8)
			{
				bits -= 8;
				output.Add((byte)((buffer >> bits) & 0xFF));
			}
		}

		if (bits > 0 && (buffer & ((1 << bits) - 1)) != 0)
			throw new KernPanic("auth: секрет: неверный Base32 (лишние биты)");
		if (output.Count == 0)
			throw new KernPanic("auth: секрет: пустая строка");
		return [.. output];
	}

	private static int Base32Value(char symbol) =>
		symbol switch
		{
			>= 'A' and <= 'Z' => symbol - 'A',
			>= 'a' and <= 'z' => symbol - 'a',
			>= '2' and <= '7' => symbol - '2' + 26,
			_ => -1,
		};

	private static byte[] SecretBytes(string secret)
	{
		byte[] key = DecodeSecret(secret);
		if (key.Length < MinimumSecretBytes)
			throw new KernPanic($"auth: секрет: слишком короткий (нужно не менее {MinimumSecretBytes} байт)");
		return key;
	}

	private static bool VerifyCode(byte[] key, string code, long counter, int window, int digits)
	{
		if (code is null || code.Length != digits)
			return false;
		var matched = false;
		for (long offset = -window; offset <= window; offset++)
		{
			long candidate = counter + offset;
			if (candidate < 0)
				continue;
			matched |= ConstantTimeEq(HotpCode(key, candidate, digits), code);
		}

		return matched;
	}

	private static string HotpCode(byte[] key, long counter, int digits)
	{
		var message = new byte[8];
		for (var index = 7; index >= 0; index--)
		{
			message[index] = (byte)(counter & 0xFF);
			counter >>= 8;
		}

		byte[] hash = HMACSHA1.HashData(key, message);
		int offset = hash[^1] & 0x0F;
		int binary = ((hash[offset] & 0x7F) << 24) |
			((hash[offset + 1] & 0xFF) << 16) |
			((hash[offset + 2] & 0xFF) << 8) |
			(hash[offset + 3] & 0xFF);
		long modulo = (long)System.Math.Pow(10, digits);
		return (binary % modulo).ToString($"D{digits}", System.Globalization.CultureInfo.InvariantCulture);
	}
}
