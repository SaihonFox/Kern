using Kern.Binding.Attributes;

namespace Kern.Lib.Backend.Http;

/// <summary>Серверная сессия (хранится в памяти, идентификатор — в cookie kern_sid). Значения — строки.</summary>
[KernType("session")]
public sealed class KernSession(string id)
{
	internal Dictionary<string, string> Data { get; } = [];
	internal DateTime LastAccess { get; set; } = DateTime.UtcNow;

	[KernProperty] public string Id { get; } = id;

	[KernMember] public string? Get(string key) => Data.GetValueOrDefault(key);
	[KernMember] public void Set(string key, string value) => Data[key] = value;
	[KernMember] public bool Remove(string key) => Data.Remove(key);
	[KernMember] public bool Has(string key) => Data.ContainsKey(key);
	[KernMember] public void Clear() => Data.Clear();
}
