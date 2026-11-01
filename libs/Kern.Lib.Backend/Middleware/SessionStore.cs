using System.Security.Cryptography;
using Kern.Lib.Backend.Http;

namespace Kern.Lib.Backend.Middleware;

/// <summary>Хранилище сессий в памяти с TTL.</summary>
internal sealed class SessionStore(TimeSpan ttl)
{
	private readonly Dictionary<string, KernSession> _sessions = [];

	public KernSession GetOrCreate(string? id, out bool created)
	{
		lock (_sessions)
		{
			DateTime now = DateTime.UtcNow;
			foreach (string dead in _sessions
				.Where(pair => now - pair.Value.LastAccess > ttl)
				.Select(pair => pair.Key)
				.ToArray())
			{
				_sessions.Remove(dead);
			}

			if (id is not null && _sessions.TryGetValue(id, out KernSession? existing))
			{
				existing.LastAccess = now;
				created = false;
				return existing;
			}

			var fresh = new KernSession(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
			_sessions[fresh.Id] = fresh;
			created = true;
			return fresh;
		}
	}
}
