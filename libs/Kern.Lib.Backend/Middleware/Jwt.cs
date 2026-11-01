using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kern.Binding.Attributes;
using Kern.Bytecode;
using Kern.Binding.Values;
using Kern.Lib.Backend.Http;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Middleware;

/// <summary>JWT HS256: заголовок <c>{"alg":"HS256","typ":"JWT"}</c>, claims sub/roles/exp.</summary>
internal static class Jwt
{
	private static string B64(byte[] data) =>
		Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	private static byte[] FromB64(string text)
	{
		text = text.Replace('-', '+').Replace('_', '/');
		text += new string('=', (4 - text.Length % 4) % 4);
		return Convert.FromBase64String(text);
	}

	private static string Sign(string data, string secret) =>
		B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(data)));

	public static string Create(string subject, IEnumerable<string> roles, string secret, long ttlSeconds)
	{
		long exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttlSeconds;
		string header = B64(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
		string payload = B64(JsonSerializer.SerializeToUtf8Bytes(new
		{
			sub = subject,
			roles = roles.ToArray(),
			exp,
		}));
		string data = header + "." + payload;
		return data + "." + Sign(data, secret);
	}

	public static (KernClaims? Claims, string? Error) Verify(string token, string secret)
	{
		string[] parts = token.Split('.');
		if (parts.Length != 3)
			return (null, "неверный формат токена");
		string expected = Sign(parts[0] + "." + parts[1], secret);
		if (!CryptographicOperations.FixedTimeEquals(
				Encoding.ASCII.GetBytes(expected),
				Encoding.ASCII.GetBytes(parts[2])))
		{
			return (null, "неверная подпись");
		}

		try
		{
			using var document = JsonDocument.Parse(FromB64(parts[1]));
			JsonElement root = document.RootElement;
			long exp = root.TryGetProperty("exp", out JsonElement expiration) ? expiration.GetInt64() : 0;
			if (exp != 0 && exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
				return (null, "срок истёк");
			string sub = root.TryGetProperty("sub", out JsonElement subject) ? subject.GetString() ?? "" : "";
			List<string> roles = root.TryGetProperty("roles", out JsonElement roleArray) &&
				roleArray.ValueKind == JsonValueKind.Array
					? [.. roleArray.EnumerateArray().Select(item => item.GetString() ?? "")]
					: [];
			return (new KernClaims(sub, roles, exp), null);
		}
		catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
		{
			return (null, "неверный токен");
		}
	}
}

/// <summary>JWT: <c>jwt.sign("имя", ["admin"], secret, 3600)</c>, <c>jwt.verify(token, secret)</c>.</summary>
[KernType("jwt")]
public static class JwtApi
{
	[KernStatic]
	public static string Sign(string subject, KList<string> roles, string secret, long ttlSeconds) =>
		Jwt.Create(subject, roles.ToArray(), secret, ttlSeconds);

	[KernStatic]
	[KernReturn("result<claims, str>")]
	public static KValue Verify(string token, string secret, Interpreter interpreter)
	{
		(KernClaims? claims, string? error) = Jwt.Verify(token, secret);
		if (claims is not null)
		{
			KType type = KernTypeHelpers.NativeType(interpreter, "claims");
			return KernResults.Ok(interpreter, type, KernTypeHelpers.Box(claims));
		}

		return KernResults.Err(
			interpreter,
			interpreter.Types.Prim(PrimKind.Str),
			KValue.Str(error ?? "неверный токен"));
	}
}
