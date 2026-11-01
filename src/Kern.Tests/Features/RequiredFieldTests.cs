using Kern.Tests.Features.Support;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>Q18: обязательные поля (<c>!</c>) должны быть присвоены при создании объекта.</summary>
public sealed class RequiredFieldTests
{
	[Fact]
	public void Missing_required_field_reports_KRN2192()
	{
		const string source = """
			type User { !str name }
			fun run() { var user = User() }
			""";

		var module = StdPipeline.CompileOrNull(source, out var diagnostics);

		Assert.Null(module);
		Assert.Contains(diagnostics, diagnostic =>
			diagnostic.Code == "KRN2192" &&
			diagnostic.Message.Contains("name", StringComparison.Ordinal));
	}

	[Fact]
	public void Constructor_and_initializer_assignments_satisfy_required_fields()
	{
		const string source = """
			type User {
				!str name
				!i32 age = 20

				ctor(str n) { $name = n }
			}

			fun run() -> str {
				var user = User("аня")
				f"${user.name}/${user.age}"
			}
			""";

		Assert.Equal(
			Kern.Vm.Values.KValue.Str("аня/20"),
			StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Chained_constructor_uses_the_root_assignment()
	{
		const string source = """
			type User {
				!str name

				ctor(str n) { $name = n }
				ctor() : $("аноним")
			}

			fun run() -> str {
				var user = User()
				user.name
			}
			""";

		Assert.Equal(Kern.Vm.Values.KValue.Str("аноним"), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Assignment_on_only_one_branch_is_not_enough()
	{
		const string source = """
			type User {
				!str name

				ctor(bool ok) {
					if ok { $name = "x" }
				}
			}

			fun run() { var user = User(true) }
			""";

		var module = StdPipeline.CompileOrNull(source, out var diagnostics);

		Assert.Null(module);
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2192");
	}
}
