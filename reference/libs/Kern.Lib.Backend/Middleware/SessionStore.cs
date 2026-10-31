using System.Security.Cryptography;
using Kern.Lib.Backend.Http;

namespace Kern.Lib.Backend.Middleware;

/// <summary>Сессии в памяти со скользящим сроком жизни.</summary>
internal sealed class SessionStore(TimeSpan ttl)
{
	private readonly Dictionary<string, KernSession> _sessions = [];

	public KernSession GetOrCreate(string? id, out bool created)
	{
		lock (_sessions)
		{
			var now = DateTime.UtcNow;
			foreach (var dead in _sessions.Where(p => now - p.Value.LastAccess > ttl).Select(p => p.Key).ToList()) _sessions.Remove(dead);
			if (id is not null && _sessions.TryGetValue(id, out var s))
			{
				s.LastAccess = now;
				created = false;
				return s;
			}
			var fresh = new KernSession(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
			_sessions[fresh.Id] = fresh;
			created = true;
			return fresh;
		}
	}
}
