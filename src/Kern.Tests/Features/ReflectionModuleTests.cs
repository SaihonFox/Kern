using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// <c>kern.reflection</c> (spec round 18): <c>type</c>, <c>field_info</c>, атрибуты,
/// вызов метода, создание экземпляра и динамический <c>define_type</c>.
/// </summary>
public sealed class ReflectionModuleTests
{
	[Fact]
	public void Type_fields_attributes_and_method_call()
	{
		const string source = """
			#[entity("users")]
			type User(str name, i32 age) {
				#[column("mail")]
				str email = ""

				fun greet(str who) -> str {
					ret f"Привет, ${who}, я ${name}"
				}
			}

			fun run() -> str {
				var t = typeof<User>()
				var u = User("Ян", 30)
				var m = t.get_method("greet")
				var v = t.create(<object>["Аня", 25])
				f"${t.name}|${m!.call(u, ["мир"])}|${t.get(v, "name")}|${t.has_attribute("entity")}"
			}
			""";

		Assert.Equal(
			KValue.Str("User|Привет, мир, я Ян|Аня|true"),
			StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Compile_time_meta_returns_lists_and_flags()
	{
		const string source = """
			#[entity("users")]
			type User(str name, i32 age)

			fun names() -> list<str> field_names<User>()
			fun attrs() -> list<str> attribute_names<User>()
			fun has() -> bool has_attribute<User>("entity")
			""";

		KValue names = StdPipeline.Run(source, "names");
		Assert.Equal(2, names.Ref is Kern.Vm.Values.KList list ? list.Count : -1);
		Assert.Equal(
			KValue.Str("entity"),
			StdPipeline.Run(source, "attrs").Ref is Kern.Vm.Values.KList{ Count: 1 } attributes
				? attributes[0]
				: KValue.Nil);
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "has"));
	}

	[Fact]
	public void Define_type_at_runtime_creates_objects()
	{
		const string source = """
			fun run() -> str {
				var builder = reflect.define_type("Point3").as_unit()
				builder = builder.field("x", f64).field("y", f64).field("z", f64)
				var pointType = builder.build()
				var point = pointType.create(<object>[1.0, 2.0, 3.0])
				f"${pointType.get(point, "z")}"
			}
			""";

		Assert.Equal(KValue.Str("3"), StdPipeline.Run(source, "run"));
	}
}
