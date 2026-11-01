using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Q19: приёмник `$` пишется только короткой формой. Q17: поля `unit!` можно заполнять
/// в конструкторе/init-контексте, но не после создания.
/// </summary>
public sealed class SelfAndImmutableTests
{
	[Fact]
	public void Long_self_form_is_rejected()
	{
		const string source = """
			type Box(i32 x) {
				fun value() -> i32 { $.x }
			}

			fun run() -> i32 Box(1).value()
			""";

		var module = StdPipeline.CompileOrNull(source, out var diagnostics);

		Assert.Null(module);
		Assert.Contains(diagnostics, diagnostic =>
			diagnostic.Code == "PAR003" &&
			diagnostic.Message.Contains("$field", StringComparison.Ordinal));
	}

	[Fact]
	public void Short_self_form_still_works()
	{
		const string source = """
			type Box(i32 x) {
				fun value() -> i32 { $x + 1 }
			}

			fun run() -> i32 Box(1).value()
			""";

		Assert.Equal(KValue.I32(2), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Immutable_unit_fields_can_be_written_in_its_constructor()
	{
		const string source = """
			unit! Lock {
				i32 value

				ctor(i32 v) { $value = v }

				fun get() -> i32 value
			}

			fun run() -> i32 Lock(5).get()
			""";

		Assert.Equal(KValue.I32(5), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Immutable_unit_fields_can_be_written_in_an_init_accessor()
	{
		const string source = """
			unit! Box {
				i32 _stored

				i32 value {
					get $_stored
					init $_stored = value
				}

				ctor(i32 v) { $value = v }
			}

			fun run() -> i32 {
				var box = Box(7)
				box.value
			}
			""";

		Assert.Equal(KValue.I32(7), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Immutable_unit_fields_stay_sealed_after_construction()
	{
		const string source = """
			unit! Lock {
				i32 value

				ctor(i32 v) { $value = v }
			}

			fun change(Lock l) { l.value = 1 }
			fun run() { }
			""";

		var module = StdPipeline.CompileOrNull(source, out var diagnostics);

		Assert.Null(module);
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2105");
	}
}
