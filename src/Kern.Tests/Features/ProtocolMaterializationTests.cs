using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Derived-протоколы <c>hash</c>/<c>compare</c>/<c>clone</c> материализуются в обычные
/// методы: они видны в рефлексии и <c>.kbc</c>, а вызов работает как раньше.
/// </summary>
public sealed class ProtocolMaterializationTests
{
	[Fact]
	public void Derived_protocols_are_visible_as_methods()
	{
		const string source = """
			#[derive(hash, compare, clone)]
			type User(str name, i32 age)

			fun run() -> str {
				var user = User("ann", 1)
				var other = User("bob", 2)
				var info = typeof<User>()
				var hashInfo = info.get_method("hash")
				var compareInfo = info.get_method("compare")
				var cloneInfo = info.get_method("clone")
				f"${hashInfo != nil}/${compareInfo != nil}/${cloneInfo != nil}/" +
					f"${info.get_methods("compare").len}/${user.compare(other) < 0}/${user.clone().name}"
			}
			""";

		Assert.Equal(KValue.Str("true/true/true/1/true/ann"), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Derived_protocols_on_unit_are_materialized()
	{
		const string source = """
			#[derive(hash, compare)]
			unit Point(i32 x, i32 y)

			fun run() -> str {
				var info = typeof<Point>()
				var a = Point(1, 2)
				var b = Point(1, 3)
				f"${info.get_method("hash") != nil}/${info.get_method("compare") != nil}/" +
					f"${a.compare(b) < 0}/${a.hash() == Point(1, 2).hash()}"
			}
			""";

		Assert.Equal(KValue.Str("true/true/true/true"), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Manual_protocol_member_is_not_duplicated()
	{
		const string source = """
			type User(str name) {
				op hash() -> u64 42
			}

			fun run() -> i32 typeof<User>().get_methods("hash").len
			""";

		Assert.Equal(KValue.I32(1), StdPipeline.Run(source, "run"));
	}
}
