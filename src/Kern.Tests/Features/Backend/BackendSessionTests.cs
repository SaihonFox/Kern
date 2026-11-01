using Kern.Lib.Backend.Http;
using Kern.Lib.Backend.Middleware;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features.Backend;

/// <summary>Сессии backend: создание, повторное использование и данные.</summary>
public sealed class BackendSessionTests
{
	[Fact]
	public void Store_creates_and_reuses_a_session()
	{
		var store = new SessionStore(TimeSpan.FromMinutes(5));

		KernSession first = store.GetOrCreate(null, out bool created);
		Assert.True(created);
		first.Set("user", "anya");

		KernSession second = store.GetOrCreate(first.Id, out bool reused);
		Assert.False(reused);
		Assert.Same(first, second);
		Assert.Equal("anya", second.Get("user"));
	}

	[Fact]
	public void Unknown_id_creates_a_new_session()
	{
		var store = new SessionStore(TimeSpan.FromMinutes(5));

		KernSession session = store.GetOrCreate("missing", out bool created);

		Assert.True(created);
		Assert.NotEqual("missing", session.Id);
	}

	[Fact]
	public void Session_members_add_read_and_remove_values()
	{
		var session = new KernSession("sid");
		var box = new KNativeBox(session);

		SessionApi.Set(box, "role", "admin");
		Assert.True(SessionApi.Has(box, "role"));
		Assert.Equal("admin", SessionApi.Get(box, "role"));

		Assert.True(SessionApi.Remove(box, "role"));
		Assert.False(SessionApi.Has(box, "role"));
		Assert.Null(SessionApi.Get(box, "role"));

		SessionApi.Set(box, "a", "1");
		SessionApi.Clear(box);
		Assert.False(SessionApi.Has(box, "a"));
	}

	[Fact]
	public void Sessions_can_be_persisted_to_a_file()
	{
		string path = Path.Combine(Path.GetTempPath(), $"kern-sessions-{Guid.NewGuid():N}.json");
		try
		{
			var store = new SessionStore(TimeSpan.FromMinutes(5), path);
			KernSession session = store.GetOrCreate(null, out _);
			session.Set("user", "anya");
			store.Save();

			var restored = new SessionStore(TimeSpan.FromMinutes(5), path);
			KernSession loaded = restored.GetOrCreate(session.Id, out bool created);

			Assert.False(created);
			Assert.Equal("anya", loaded.Get("user"));
		}
		finally
		{
			if (File.Exists(path))
				File.Delete(path);
		}
	}
}
