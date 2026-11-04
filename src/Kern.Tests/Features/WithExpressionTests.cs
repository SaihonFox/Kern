using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// M14, <c>with</c>-выражение (spec round 27): копия unit/type с заменёнными полями,
/// исходное значение не меняется, вложенные with допустимы.
/// </summary>
public sealed class WithExpressionTests
{
	[Fact]
	public void Unit_with_replaces_fields_and_keeps_the_original()
	{
		const string source = """
			unit Point(i32 x, i32 y)

			fun run() -> str {
				var p = Point(1, 2)
				var q = p:{ x = 10 }
				f"${p.x},${p.y}/${q.x},${q.y}"
			}
			""";

		Assert.Equal(KValue.Str("1,2/10,2"), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Type_with_replaces_fields_and_keeps_the_original()
	{
		const string source = """
			type User(str name, i32 age)

			fun run() -> str {
				var a = User("аня", 20)
				var b = a:{ age = a.age + 1 }
				f"${a.age}/${b.age}/${a.name}/${b.name}"
			}
			""";

		Assert.Equal(KValue.Str("20/21/аня/аня"), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void With_sees_inherited_fields_and_nests()
	{
		const string source = """
			type Base { i32 id = 1 }
			type User(str name) [Base] { }

			fun run() -> str {
				var u = User("x")
				var v = u:{ id = 5, name = "y" }
				var w = v:{ id = v.id + 1 }
				f"${v.id}/${v.name}/${u.id}/${u.name}/${w.id}"
			}
			""";

		Assert.Equal(KValue.Str("5/y/1/x/6"), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void With_works_as_a_dictionary_value()
	{
		const string source = """
			unit Point(i32 x, i32 y)

			fun run() -> str {
				var p = Point(1, 2)
				dict<str, Point> values = ["p": p:{ x = 3 }]
				f"${values["p"].x}/${p.x}"
			}
			""";

		Assert.Equal(KValue.Str("3/1"), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void With_replaces_writable_properties()
	{
		const string source = """
			type User(str name) {
				i32 age { get; set } = 1
			}

			fun run() -> str {
				var a = User("аня")
				var b = a:{ age = 30 }
				f"${a.age}/${b.age}/${b.name}"
			}
			""";

		Assert.Equal(KValue.Str("1/30/аня"), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void With_rejects_read_only_properties()
	{
		const string source = """
			type User {
				i32 age { get; set } = 1
				i32 doubled { get age * 2 }
			}

			fun run() {
				var a = User()
				var b = a:{ doubled = 4 }
			}
			""";

		var module = StdPipeline.CompileOrNull(source, out var diagnostics);
		Assert.Null(module);
		Assert.Contains(diagnostics, diagnostic =>
			diagnostic.Code == "KRN2105" && diagnostic.Message.Contains("только для чтения", StringComparison.Ordinal));
	}

	[Fact]
	public void With_rejects_unknown_static_duplicate_and_immutable_fields()
	{
		const string source = """
			unit! Lock(i32 n)
			type Counter { #i32 total = 0; i32 value = 1 }

			fun run() {
				var l = Lock(1)
				var m = l:{ n = 2 }
				var c = Counter()
				var d = c:{ total = 1 }
				var e = c:{ value = 2, value = 3 }
				var f = c:{ missing = 4 }
			}
			""";

		var module = StdPipeline.CompileOrNull(source, out var diagnostics);

		Assert.Null(module);
		Assert.Contains(diagnostics, diagnostic =>
			diagnostic.Code == "KRN2101" && diagnostic.Message.Contains("unit!", StringComparison.Ordinal));
		Assert.Contains(diagnostics, diagnostic =>
			diagnostic.Code == "KRN2101" && diagnostic.Message.Contains("статическое", StringComparison.Ordinal));
		Assert.Contains(diagnostics, diagnostic =>
			diagnostic.Code == "KRN2106" && diagnostic.Message.Contains("несколько раз", StringComparison.Ordinal));
		Assert.Contains(diagnostics, diagnostic =>
			diagnostic.Code == "KRN2101" && diagnostic.Message.Contains("нет поля 'missing'", StringComparison.Ordinal));
	}
}
