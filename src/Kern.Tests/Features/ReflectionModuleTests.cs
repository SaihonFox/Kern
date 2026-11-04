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
	public void Full_type_surface_reads_members_and_flags()
	{
		const string source = """
			#[entity("users")]
			type User(str name, i32 age) {
				str email = ""

				fun greet(str who) -> str {
					f"привет ${who}"
				}
			}

			fun run() -> str {
				var t = typeof<User>()
				var u = User("Ян", 30)
				t.set(u, "name", "Аня")
				var fieldValue = t.get_field("name")
				var method = t.get_method("greet")
				f"${t.name}/${t.full_name}/${t.fields.len}/${t.methods.len}|" +
					f"${fieldValue!.get(u)}|${method!.params.len}|${t.get_methods("greet").len}|" +
					f"${t.is_unit}/${t.is_abstract}/${t.is_declared}|${t.has_attribute("entity")}|" +
					f"${t.is_instance(u)}|${t.is_assignable_from(typeof<User>())}"
			}
			""";

		Assert.Equal(
			KValue.Str("User/main.User/3/1|Аня|1|1|false/false/true|true|true|true"),
			StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Module_reflection_lists_types_and_functions()
	{
		const string source = """
			#[entity("users")]
			type User(str name)

			#[entity("users")]
			fun find() -> i32 1

			fun run() -> str {
				var types = reflect.types()
				var functions = reflect.functions()
				f"${types.len > 0}/${reflect.find_type("User") != nil}/" +
					f"${functions.len > 0}/${reflect.find_function("find") != nil}/" +
					f"${reflect.functions_with_attribute("entity").len}/" +
					f"${reflect.module_attributes().len}"
			}
			""";

		Assert.Equal(KValue.Str("true/true/true/true/1/0"), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Compile_time_names_cover_properties_methods_and_parents()
	{
		const string source = """
			type Base { i32 id = 1 }
			type User(str name) [Base] {
				str email => ""
				fun greet -> str name
			}

			fun props() -> list<str> property_names<User>()
			fun methods() -> list<str> method_names<User>()
			fun parents() -> list<str> parent_names<User>()
			""";

		Assert.Equal(KValue.I32(1), StdPipeline.Run("fun count() -> i32 property_names<User>().len" + Environment.NewLine + "type User(str name) [Base] { str email => \"\" }" + Environment.NewLine + "type Base { i32 id = 1 }", "count"));
		KValue methods = StdPipeline.Run(source, "methods");
		Assert.True(methods.Ref is Kern.Vm.Values.KList { Count: >= 1 });
		KValue parentList = StdPipeline.Run(source, "parents");
		Assert.True(parentList.Ref is Kern.Vm.Values.KList { Count: 1 });
	}

	[Fact]
	public void Define_type_adds_attributes_and_field_attributes()
	{
		const string source = """
			fun run() -> str {
				var builder = reflect.define_type("Point3").as_unit()
				builder = builder.field("x", f64).field_attribute("column")
				builder = builder.field("y", f64)
				builder = builder.attribute("entity", ["points"])
				var pointType = builder.build()
				var point = pointType.create(<object>[1.0, 2.0])
				f"${pointType.name}/${pointType.fields.len}/${pointType.has_attribute("entity")}/${pointType.get(point, "x")}"
			}
			""";

		Assert.Equal(KValue.Str("Point3/2/true/1"), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Property_info_reads_and_sets_fields()
	{
		const string source = """
			type User {
				i32 x = 1
				i32 double => x * 2
			}

			fun run() -> str {
				var t = typeof<User>()
				var u = User()
				var property = t.get_property("double")
				f"${property!.get(u)}/${t.get_field("x")!.get(u)}"
			}
			""";

		Assert.Equal(KValue.Str("2/1"), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Method_info_calls_static_functions_and_exposes_params()
	{
		const string source = """
			fun add(i32 a, i32 b) -> i32 a + b

			fun run() -> str {
				var info = reflect.find_function("add")
				f"${info!.params.len}/${info!.call_static([2, 3])}"
			}
			""";

		Assert.Equal(KValue.Str("2/5"), StdPipeline.Run(source, "run"));
	}

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
	public void Type_reflection_sees_inherited_fields_and_methods()
	{
		const string source = """
			type Base {
				i32 x = 1

				fun describe() -> i32 x
			}

			type Child()[Base] {
				i32 y = 2
			}

			fun run() -> str {
				var info = typeof<Child>()
				var child = Child()
				f"${info.fields.len}/${info.get_method("describe") != nil}/" +
					f"${info.get_methods("describe").len}/${info.get(child, "x")}"
			}
			""";

		Assert.Equal(KValue.Str("2/true/1/1"), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Define_type_supports_methods_and_properties()
	{
		const string source = """
			fun run() -> str {
				list<object> no_params = []
				list<object> no_args = []
				var builder = reflect.define_type("Point").as_unit().field("x", f64)
				builder = builder.method("double", no_params, f64, fun (object self) -> f64 42.0)
				builder = builder.property("value", f64, fun (object self) -> f64 7.0)
				builder = builder.property("value2", f64, fun (object self) -> f64 1.0,
					fun (object self, f64 v) { })
				var pointType = builder.build()
				var point = pointType.create(<object>[1.0])
				var method = pointType.get_method("double")
				var property = pointType.get_property("value")
				f"${pointType.methods.len}/${pointType.properties.len}/" +
					f"${method!.call(point, no_args).to_str()}/${property!.get(point).to_str()}/" +
					f"${method!.params.len}/${method!.return_type.name}"
			}
			""";

		Assert.Equal(KValue.Str("1/2/42/7/0/f64"), StdPipeline.Run(source, "run"));
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
