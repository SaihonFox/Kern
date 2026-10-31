using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kern.Lib.Backend.Http;

namespace Kern.Lib.Backend.Middleware;

/// <summary>JWT HS256 без внешних зависимостей: заголовок {"alg":"HS256","typ":"JWT"}, claims sub/roles/exp.</summary>
internal static class Jwt
{
	private static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	private static byte[] FromB64(string s)
	{
		s = s.Replace('-', '+').Replace('_', '/');
		s += new string('=', (4 - s.Length % 4) % 4);
		return Convert.FromBase64String(s);
	}

	private static string Sign(string data, string secret) =>
		B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(data)));

	public static string Create(string subject, IEnumerable<string> roles, string secret, long ttlSeconds)
	{
		long exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttlSeconds;
		string header = B64(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
		string payload = B64(JsonSerializer.SerializeToUtf8Bytes(new { sub = subject, roles = roles.ToArray(), exp }));
		string data = header + "." + payload;
		return data + "." + Sign(data, secret);
	}

	public static (KernClaims? Claims, string? Error) Verify(string token, string secret)
	{
		var parts = token.Split('.');
		if (parts.Length != 3) return (null, "неверный формат токена");
		var expected = Sign(parts[0] + "." + parts[1], secret);
		if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[2])))
			return (null, "неверная подпись");
		try
		{
			using var doc = JsonDocument.Parse(FromB64(parts[1]));
			var root = doc.RootElement;
			long exp = root.TryGetProperty("exp", out var e) ? e.GetInt64() : 0;
			if (exp != 0 && exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return (null, "токен истёк");
			string sub = root.TryGetProperty("sub", out var s) ? s.GetString() ?? "" : "";
			var roles = root.TryGetProperty("roles", out var r) && r.ValueKind == JsonValueKind.Array
				? r.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
			return (new KernClaims(sub, roles, exp), null);
		}
		catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
		{
			return (null, "неверный токен");
		}
	}
}
