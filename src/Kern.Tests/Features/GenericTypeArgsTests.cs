using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Runtime-аргументы generic-функций (2026-11-04): <c>typeof&lt;T&gt;()</c> получает
/// конкретный тип, в том числе через вложенный generic-вызов с <c>T</c>.
/// </summary>
public sealed class GenericTypeArgsTests
{
	[Fact]
	public void Typeof_type_parameter_uses_runtime_arguments()
	{
		const string source = """
			fun id<T>(T value) -> object typeof<T>()
			fun wrap<T>(T value) -> object id<T>(value)

			fun direct() -> str id<i32>(1).name
			fun inferred() -> str id("x").name
			fun forwarded() -> str wrap<str>("x").name

			type Box() {
				fun method<T>(T value) -> object typeof<T>()
			}
			fun virtual_method() -> str {
				Box box = Box()
				box.method<str>("x").name
			}
			""";

		Assert.Equal(KValue.Str("i32"), StdPipeline.Run(source, "direct"));
		Assert.Equal(KValue.Str("str"), StdPipeline.Run(source, "inferred"));
		Assert.Equal(KValue.Str("str"), StdPipeline.Run(source, "forwarded"));
		Assert.Equal(KValue.Str("str"), StdPipeline.Run(source, "virtual_method"));
	}
}
