using Kern.Lib.Backend.Routing;
using Xunit;

namespace Kern.Tests.Features.Backend;

/// <summary>Маршруты backend: литералы, параметры, хвост и specificity.</summary>
public sealed class BackendRouteTests
{
	[Fact]
	public void Literal_and_parameter_segments_match()
	{
		var pattern = new RoutePattern("/users/{id}");

		Assert.True(pattern.Match("/users/42", out Dictionary<string, string> values));
		Assert.Equal("42", values["id"]);
		Assert.False(pattern.Match("/users/42/extra", out _));
		Assert.False(pattern.Match("/accounts/42", out _));
	}

	[Fact]
	public void Rest_segment_captures_the_remaining_path()
	{
		var pattern = new RoutePattern("/assets/{*path}");

		Assert.True(pattern.Match("/assets/css/app.css", out Dictionary<string, string> values));
		Assert.Equal("css/app.css", values["path"]);
		Assert.True(pattern.Match("/assets/", out Dictionary<string, string> empty));
		Assert.Equal("", empty["path"]);
	}

	[Fact]
	public void Specificity_prefers_literals_over_parameters()
	{
		var literal = new RoutePattern("/users/me");
		var parameter = new RoutePattern("/users/{id}");
		var rest = new RoutePattern("/users/{*path}");

		Assert.True(literal.Specificity > parameter.Specificity);
		Assert.True(parameter.Specificity > rest.Specificity);
	}

	[Fact]
	public void Join_normalizes_slashes()
	{
		Assert.Equal("/api/users", RoutePattern.Join("/api", "users"));
		Assert.Equal("/api/users", RoutePattern.Join("/api/", "/users"));
		Assert.Equal("/users", RoutePattern.Join("", "/users"));
		Assert.Equal("/api", RoutePattern.Join("/api", ""));
	}

	[Theory]
	[InlineData("/users/%D0%90%D0%BD%D1%8F", "Аня")]
	[InlineData("/users/a%20b", "a b")]
	public void Escaped_segments_are_decoded(string path, string expected)
	{
		var pattern = new RoutePattern("/users/{name}");

		Assert.True(pattern.Match(path, out Dictionary<string, string> values));
		Assert.Equal(expected, values["name"]);
	}
}
