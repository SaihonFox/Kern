using System.Text;
using Kern.Lib.Backend.Http;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features.Backend;

/// <summary>Ответы backend: фабрики, заголовки, cookie и файлы.</summary>
public sealed class BackendResTests
{
	[Fact]
	public void Factories_set_status_and_content_type()
	{
		KernRes text = ResApi.Text("hi");
		Assert.Equal(200, text.Status);
		Assert.Equal("hi", text.Body);
		Assert.Contains("text/plain", text.ContentType);

		KernRes html = ResApi.Html("<b>x</b>");
		Assert.Contains("text/html", html.ContentType);

		KernRes noContent = ResApi.NoContent();
		Assert.Equal(204, noContent.Status);
		Assert.Empty(noContent.Bytes);

		KernRes notFound = ResApi.NotFound();
		Assert.Equal(404, notFound.Status);
		Assert.Contains("\"error\"", notFound.Body);

		Assert.Equal(400, ResApi.BadRequest("bad").Status);
		Assert.Equal(401, ResApi.Unauthorized().Status);
		Assert.Equal(403, ResApi.Forbidden().Status);
		Assert.Equal(418, ResApi.Send(418, "tea").Status);
	}

	[Fact]
	public void Redirect_header_and_cookie_are_recorded()
	{
		KernRes res = ResApi.Redirect("/login");
		Assert.Equal(302, res.Status);
		Assert.Equal("/login", res.Headers["Location"]);

		var box = new KNativeBox(res);
		Assert.Same(res, ResApi.Header(box, "X-Test", "1"));
		Assert.Equal("1", res.Headers["X-Test"]);

		Assert.Same(res, ResApi.Cookie(box, "kern_sid", "a b", 60));
		Assert.Contains("kern_sid=a%20b", res.Cookies[0]);
		Assert.Contains("Max-Age=60", res.Cookies[0]);
	}

	[Fact]
	public void Status_content_type_and_body_can_be_changed()
	{
		KernRes res = ResApi.Text("old");
		var box = new KNativeBox(res);

		ResApi.Status(box, 201);
		ResApi.ContentType(box, "application/x-test");
		ResApi.Body(box, "new");

		Assert.Equal(201, ResApi.Status(box));
		Assert.Equal("application/x-test", ResApi.ContentType(box));
		Assert.Equal("new", ResApi.Body(box));
		Assert.Equal("new", Encoding.UTF8.GetString(res.Bytes));
	}

	[Fact]
	public void File_reads_bytes_and_missing_file_is_404()
	{
		string path = Path.Combine(Path.GetTempPath(), $"kern-backend-{Guid.NewGuid():N}.txt");
		System.IO.File.WriteAllText(path, "file-body", new UTF8Encoding(false));
		try
		{
			KernRes found = ResApi.File(path);
			Assert.Equal(200, found.Status);
			Assert.Equal("file-body", Encoding.UTF8.GetString(found.Bytes));
			Assert.Contains("text/plain", found.ContentType);

			KernRes missing = ResApi.File(path + ".missing");
			Assert.Equal(404, missing.Status);
		}
		finally
		{
			System.IO.File.Delete(path);
		}
	}
}
