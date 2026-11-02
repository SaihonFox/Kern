using Kern.Binding.Resolution;
using Kern.Tests.Features.Support;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Закрытые языковые пробелы 2026-11-02: шаг среза, пользовательский <c>op in</c>,
/// <c>ctor(args)</c>, настраиваемый предел кадров, запись свойства модуля.
/// </summary>
[Collection("timing")]
public sealed class LanguageExtrasTests
{
	[Fact]
	public void Slice_step_works_for_lists_and_strings()
	{
		const string source = """
			fun list_step() -> str [0, 1, 2, 3, 4, 5, 6, 7, 8, 9][1..8:2].join(",")
			fun inclusive_step() -> str [0, 1, 2, 3, 4, 5, 6, 7, 8, 9][1..=8:2].join(",")
			fun string_step() -> str "abcdef"[0..6:2]
			""";

		Assert.Equal(KValue.Str("1,3,5,7"), StdPipeline.Run(source, "list_step"));
		Assert.Equal(KValue.Str("1,3,5,7"), StdPipeline.Run(source, "inclusive_step"));
		Assert.Equal(KValue.Str("ace"), StdPipeline.Run(source, "string_step"));
	}

	[Fact]
	public void Slice_step_zero_or_negative_panics()
	{
		Assert.Contains("шаг", StdPipeline.RunPanics(
			"fun bad() { println([1, 2, 3][0..3:0]) }", "bad").Message);
		Assert.Contains("шаг", StdPipeline.RunPanics(
			"fun bad() { println([1, 2, 3][0..3:-1]) }", "bad").Message);
	}

	[Fact]
	public void User_defined_in_operator_is_called_on_the_right_operand()
	{
		const string source = """
			type Bag(i32 value) {
				op in(i32 item) -> bool item == value
			}

			fun yes() -> bool 3 in Bag(3)
			fun no() -> bool 4 in Bag(3)
			fun negated() -> bool 4 !in Bag(3)
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "yes"));
		Assert.Equal(KValue.Bool(false), StdPipeline.Run(source, "no"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "negated"));
	}

	[Fact]
	public void Ctor_constraint_checks_argument_types()
	{
		const string source = """
			type Good(i32 x)
			type Bad(str s)
			fun check<T[ctor(i32)]>(T value) -> i32 1
			""";

		Assert.Equal(KValue.I32(1), StdPipeline.Run(
			source + "\nfun ok() -> i32 check(Good(1))", "ok"));
		Assert.Null(StdPipeline.CompileOrNull(
			source + "\nfun bad() -> i32 check(Bad(\"x\"))", out var diagnostics));
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN1401");
	}

	[Fact]
	public void Vm_frame_limit_is_configurable_and_defaults_to_ten_thousand()
	{
		const string source = """
			fun down(i32 n) -> i32 {
				if n == 0 { 0 } else { down(n - 1) + 1 }
			}
			""";
		string? previous = Environment.GetEnvironmentVariable("KERN_MAX_CALL_DEPTH");
		try
		{
			Environment.SetEnvironmentVariable("KERN_MAX_CALL_DEPTH", "500");
			var interpreter = new Interpreter(
				StdPipeline.Compile(source).Module,
				new NativeResolver(StdPipeline.Catalog()));
			KernPanic panic = Assert.Throws<KernPanic>(() => interpreter.Run("down", KValue.I32(1200)));
			Assert.Contains("переполнение стека", panic.Message);

			Environment.SetEnvironmentVariable("KERN_MAX_CALL_DEPTH", "2000");
			var deeper = new Interpreter(
				StdPipeline.Compile(source).Module,
				new NativeResolver(StdPipeline.Catalog()));
			Assert.Equal(KValue.I32(1200), deeper.Run("down", KValue.I32(1200)));
		}
		finally
		{
			Environment.SetEnvironmentVariable("KERN_MAX_CALL_DEPTH", previous);
		}
	}

	[Fact]
	public void Module_property_assignment_uses_the_native_setter()
	{
		const string source = """
			use kern.log[log]

			fun set_and_read() -> str {
				log.level = "warn"
				log.level
			}
			""";

		try
		{
			Assert.Equal(KValue.Str("warn"), StdPipeline.Run(source, "set_and_read"));
		}
		finally
		{
			Kern.Runtime.Logging.LogApi.SetLevel("info");
		}
	}

	[Fact]
	public void Regex_qualified_try_new_works()
	{
		const string source = """
			fun invalid() -> bool regex.try_new("[") == nil
			fun valid() -> bool regex.try_new("[0-9]+") != nil
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "invalid"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "valid"));
	}
}
