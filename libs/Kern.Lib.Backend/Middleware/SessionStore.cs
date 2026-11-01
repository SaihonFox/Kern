using System.Security.Cryptography;
using System.Text.Json;
using Kern.Lib.Backend.Http;

namespace Kern.Lib.Backend.Middleware;

/// <summary>
/// Хранилище сессий: в памяти или (при заданном пути) в JSON-файле.
/// </summary>
internal sealed class SessionStore
{
	private sealed record Stored(string Id, DateTime LastAccess, Dictionary<string, string> Data);

	private readonly TimeSpan _ttl;
	private readonly string? _path;
	private readonly Dictionary<string, KernSession> _sessions = [];

	public SessionStore(TimeSpan ttl, string? path = null)
	{
		_ttl = ttl;
		_path = path;
		Load();
	}

	public KernSession GetOrCreate(string? id, out bool created)
	{
		lock (_sessions)
		{
			DateTime now = DateTime.UtcNow;
			foreach (string dead in _sessions
				.Where(pair => now - pair.Value.LastAccess > _ttl)
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

	public void Save()
	{
		if (_path is null)
			return;
		try
		{
			Dictionary<string, Stored> data;
			lock (_sessions)
			{
				data = _sessions.ToDictionary(
					pair => pair.Key,
					pair => new Stored(pair.Value.Id, pair.Value.LastAccess, new Dictionary<string, string>(pair.Value.Data)),
					StringComparer.Ordinal);
			}

			string? directory = Path.GetDirectoryName(_path);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);
			string temporary = _path + ".tmp";
			File.WriteAllText(temporary, JsonSerializer.Serialize(data));
			File.Move(temporary, _path, overwrite: true);
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or JsonException)
		{
			// Ошибка записи не должна ломать запрос; сессии останутся в памяти.
		}
	}

	private void Load()
	{
		if (_path is null || !File.Exists(_path))
			return;
		try
		{
			Dictionary<string, Stored>? data =
				JsonSerializer.Deserialize<Dictionary<string, Stored>>(File.ReadAllText(_path));
			if (data is null)
				return;
			DateTime now = DateTime.UtcNow;
			foreach ((string id, Stored stored) in data)
			{
				if (now - stored.LastAccess > _ttl)
					continue;
				var session = new KernSession(id) { LastAccess = stored.LastAccess };
				foreach ((string key, string value) in stored.Data)
					session.Data[key] = value;
				_sessions[id] = session;
			}
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
		{
			// Повреждённый файл игнорируется: стартуем с пустыми сессиями.
		}
	}
}
