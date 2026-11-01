using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Http;

/// <summary>Сессия (хранится в памяти процесса, идентификатор — cookie <c>kern_sid</c>).</summary>
[KernType("session")]
public sealed class KernSession(string id)
{
	internal Dictionary<string, string> Data { get; } = [];
	internal DateTime LastAccess { get; set; } = DateTime.UtcNow;

	public string Id { get; } = id;

	public string? Get(string key) => Data.GetValueOrDefault(key);

	public void Set(string key, string value) => Data[key] = value;

	public bool Remove(string key) => Data.Remove(key);

	public bool Has(string key) => Data.ContainsKey(key);

	public void Clear() => Data.Clear();

	internal static KernSession Require(KNativeBox self) => self.Value switch
	{
		KernSession session => session,
		_ => throw new KernPanic($"session: ожидалась сессия, получено {self.Value.GetType().Name}"),
	};
}

[KernType("session")]
public static class SessionApi
{
	[KernProperty]
	public static string Id(KNativeBox self) => KernSession.Require(self).Id;

	[KernMember]
	public static string? Get(KNativeBox self, string key) => KernSession.Require(self).Get(key);

	[KernMember]
	public static void Set(KNativeBox self, string key, string value) => KernSession.Require(self).Set(key, value);

	[KernMember]
	public static bool Remove(KNativeBox self, string key) => KernSession.Require(self).Remove(key);

	[KernMember]
	public static bool Has(KNativeBox self, string key) => KernSession.Require(self).Has(key);

	[KernMember]
	public static void Clear(KNativeBox self) => KernSession.Require(self).Clear();
}

/// <summary>Проверенные JWT-claims.</summary>
[KernType("claims")]
public sealed class KernClaims(string subject, List<string> roles, long expires)
{
	public string Sub { get; } = subject;
	public long Exp { get; } = expires;

	internal List<string> RoleList { get; } = roles;

	internal static KernClaims Require(KNativeBox self) => self.Value switch
	{
		KernClaims claims => claims,
		_ => throw new KernPanic($"claims: ожидались claims, получено {self.Value.GetType().Name}"),
	};
}

[KernType("claims")]
public static class ClaimsApi
{
	[KernProperty]
	public static string Sub(KNativeBox self) => KernClaims.Require(self).Sub;

	[KernProperty]
	[KernReturn("list<str>")]
	public static KValue Roles(KNativeBox self) => KernTypeHelpers.StringList(KernClaims.Require(self).RoleList);

	[KernProperty]
	public static long Exp(KNativeBox self) => KernClaims.Require(self).Exp;
}
