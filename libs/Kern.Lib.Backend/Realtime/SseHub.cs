namespace Kern.Lib.Backend.Realtime;

internal sealed record SseEvent(long Id, string Event, string Data);

/// <summary>
/// История событий одного SSE-маршрута. По <c>Last-Event-ID</c> клиент получает
/// пропущенные события; хранится последнее окно (256 записей).
/// </summary>
internal sealed class SseHub
{
	private const int ReplayLimit = 256;
	private readonly List<SseEvent> _events = [];
	private long _lastId;

	public long LastId => _lastId;

	public long Next(string eventName, string data)
	{
		long id = ++_lastId;
		_events.Add(new SseEvent(id, eventName, data));
		if (_events.Count > ReplayLimit)
			_events.RemoveRange(0, _events.Count - ReplayLimit);
		return id;
	}

	public IReadOnlyList<SseEvent> After(long id) =>
		id <= 0 ? _events.ToArray() : [.. _events.Where(item => item.Id > id)];
}
