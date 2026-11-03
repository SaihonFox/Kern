using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Модульные атрибуты <c>#[module(...)]</c>: синтаксис, сборка символа, <c>.kbc</c>
/// и <c>reflect.module_attributes()</c>.
/// </summary>
[Collection("timing")]
public sealed class ModuleAttributeTests
{
	[Fact]
	public void Module_attribute_before_project_is_emitted()
	{
		const string source = """
			#[module(author = "kern", version = "1.0")]
			project demo.mod:
			fun main() -> i32 reflect.module_attributes().len
			""";

		Assert.Equal(KValue.I32(1), StdPipeline.Run(source, "main"));
	}

	[Fact]
	public void Leading_module_attribute_works_without_project()
	{
		const string source = """
			#[module(author = "kern")]
			fun name() -> str reflect.module_attributes()[0].name
			fun main() -> i32 0
			""";

		Assert.Equal(KValue.Str("module"), StdPipeline.Run(source, "name"));
	}

	[Fact]
	public void Ordinary_attributes_still_attach_to_the_first_declaration()
	{
		const string source = """
			#[module(author = "kern")]
			#[derive(to_str)]
			unit Point(i32 x, i32 y)
			fun main() -> i32 reflect.module_attributes().len
			""";

		Assert.Equal(KValue.I32(1), StdPipeline.Run(source, "main"));
	}
}
