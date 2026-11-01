using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>Q25/B8b: `to_str` и `to_debug`, кавычки у вложенных str/char.</summary>
public sealed class FormattingConventionTests
{
	[Fact]
	public void Nested_strings_are_quoted_in_collections_tuples_and_fields()
	{
		const string source = """
			unit User(str name, i32 age)

			fun run() -> str {
				var user = User("аня", 25)
				var tuple = ("аня", 25)
				var dict = ["key": "value"]
				f"${user}/|${tuple}/|${dict}/|${["а", "б"]}"
			}
			""";

		Assert.Equal(
			KValue.Str("User(name=\"аня\", age=25)/|(\"аня\", 25)/|{\"key\": \"value\"}/|[\"а\", \"б\"]"),
			StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Derived_to_str_and_to_debug_follow_the_convention()
	{
		const string source = """
			#[derive(to_str, to_debug)]
			unit User(str name)

			fun run() -> str {
				f"${User("аня").to_str()}/|${User("аня").to_debug()}/|${"x".to_debug()}"
			}
			""";

		Assert.Equal(
			KValue.Str("User(name=\"аня\")/|User(name=\"аня\")/|\"x\""),
			StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Manual_to_str_conflicts_with_the_derive()
	{
		const string source = """
			#[derive(to_str)]
			type User(str name) {
				fun to_str() -> str "manual"
			}

			fun run() -> str User("аня").to_str()
			""";

		var module = StdPipeline.CompileOrNull(source, out var diagnostics);

		Assert.Null(module);
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2106");
	}
}
