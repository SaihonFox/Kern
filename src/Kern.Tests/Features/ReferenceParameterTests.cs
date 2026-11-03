using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Параметры <c>&amp;T</c> (in) и <c>*T</c> (ref, spec §6): <c>&amp;</c> — только чтение,
/// <c>*</c> — изменяемая ссылка на переменную/поле; <c>out</c> выполняет <c>*T</c>.
/// </summary>
public sealed class ReferenceParameterTests
{
	private const string Source = """
		fun add_into(*i32 target, i32 delta) {
			target += delta
		}

		fun swap(*i32 left, *i32 right) {
			i32 temporary = left
			left = right
			right = temporary
		}

		fun double_in_place(*i32 value) {
			value *= 2
		}

		fun forward(*i32 value) {
			add_into(*value, 1)
		}

		fun total(&list<i32> values) -> i32 {
			var sum = 0
			loop var value in values { sum += value }
			sum
		}

		fun text_len(&str text) -> i32 text.len

		unit Cell(i32 value)

		fun bump_cell(*Cell cell) {
			cell.value += 1
		}

		fun ref_arithmetic() -> i32 {
			i32 x = 10
			add_into(*x, 5)
			x
		}

		fun ref_swap() -> str {
			i32 a = 1
			i32 b = 2
			swap(*a, *b)
			f"${a},${b}"
		}

		fun ref_double() -> i32 {
			i32 x = 7
			double_in_place(*x)
			x
		}

		fun ref_forward() -> i32 {
			i32 x = 1
			forward(*x)
			x
		}

		fun ref_field() -> i32 {
			Cell c = Cell(1)
			bump_cell(*c)
			c.value
		}

		fun in_total() -> i32 {
			list<i32> xs = [1, 2, 3]
			total(&xs)
		}

		fun in_literal() -> i32 text_len(&"hello")

		fun set_to_42(*i32 value) {
			value = 42
		}

		fun add_both(*i32 left, *i32 right) {
			left += 1
			right += 1
		}

		fun out_style() -> i32 {
			i32 x = 0
			set_to_42(*x)
			x
		}

		fun aliasing() -> i32 {
			i32 x = 0
			add_both(*x, *x)
			x
		}

		fun set_with_out(out i32 value) {
			value = 7
		}

		fun out_keyword() -> i32 {
			i32 x = 0
			set_with_out(out x)
			x
		}

		fun make_counter() -> fun() -> i32 {
			var count = 0

			fun bump() -> i32 {
				add_into(*count, 1)
				count
			}

			bump
		}

		fun captured_ref() -> i32 {
			var step = make_counter()
			step()
			step()
			step()
		}

		type Registry {
			#i32 total = 0

			#fun add_one() {
				add_into(*total, 1)
			}
		}

		fun static_field() -> i32 {
			Registry.add_one()
			Registry.add_one()
			Registry.total
		}
		""";

	[Fact]
	public void Ref_parameters_write_through_the_caller_place()
	{
		Assert.Equal(KValue.I32(15), StdPipeline.Run(Source, "ref_arithmetic"));
		Assert.Equal(KValue.Str("2,1"), StdPipeline.Run(Source, "ref_swap"));
		Assert.Equal(KValue.I32(14), StdPipeline.Run(Source, "ref_double"));
		Assert.Equal(KValue.I32(2), StdPipeline.Run(Source, "ref_forward"));
	}

	[Fact]
	public void Ref_parameters_work_with_fields_and_in_parameters_read_values()
	{
		Assert.Equal(KValue.I32(2), StdPipeline.Run(Source, "ref_field"));
		Assert.Equal(KValue.I32(6), StdPipeline.Run(Source, "in_total"));
		Assert.Equal(KValue.I32(5), StdPipeline.Run(Source, "in_literal"));
	}

	[Fact]
	public void Ref_covers_out_aliasing_and_static_fields()
	{
		Assert.Equal(KValue.I32(42), StdPipeline.Run(Source, "out_style"));
		Assert.Equal(KValue.I32(2), StdPipeline.Run(Source, "aliasing"));
		Assert.Equal(KValue.I32(2), StdPipeline.Run(Source, "static_field"));
		Assert.Equal(KValue.I32(7), StdPipeline.Run(Source, "out_keyword"));
		Assert.Equal(KValue.I32(3), StdPipeline.Run(Source, "captured_ref"));
	}

	[Fact]
	public void Ref_requires_the_star_argument()
	{
		var module = StdPipeline.CompileOrNull(
			"""
			fun take(*i32 value) { value += 1 }
			fun main() {
				i32 x = 1
				take(x)
			}
			""",
			out var diagnostics);

		Assert.Null(module);
		Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("передайте '*переменная'"));
	}

	[Fact]
	public void In_parameters_are_read_only()
	{
		var module = StdPipeline.CompileOrNull(
			"""
			fun clear(&i32 value) { value = 0 }
			""",
			out var diagnostics);

		Assert.Null(module);
		Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("только чтение"));
	}

	[Fact]
	public void Reference_markers_are_rejected_outside_parameters_and_calls()
	{
		var module = StdPipeline.CompileOrNull(
			"""
			fun bad() -> &i32 0
			""",
			out var diagnostics);

		Assert.Null(module);
		Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("только в типе параметра"));
	}

	[Fact]
	public void Ref_is_rejected_in_async_generators_lambdas_and_memo()
	{
		var asyncModule = StdPipeline.CompileOrNull(
			"""
			async fun update(*i32 value) { value += 1 }
			""",
			out var asyncDiagnostics);
		Assert.Null(asyncModule);
		Assert.Contains(asyncDiagnostics, diagnostic => diagnostic.Message.Contains("async"));

		var lambdaModule = StdPipeline.CompileOrNull(
			"""
			fun main() {
				fun(*i32 value) { value += 1 }
			}
			""",
			out var lambdaDiagnostics);
		Assert.Null(lambdaModule);
		Assert.True(lambdaDiagnostics.Count > 0);

		var memoModule = StdPipeline.CompileOrNull(
			"""
			#[memo] fun update(*i32 value) { value += 1 }
			""",
			out var memoDiagnostics);
		Assert.Null(memoModule);
		Assert.Contains(memoDiagnostics, diagnostic => diagnostic.Message.Contains("#[memo]"));
	}

	[Fact]
	public void Ref_marker_on_a_value_parameter_is_rejected()
	{
		var module = StdPipeline.CompileOrNull(
			"""
			fun plain(i32 value) -> i32 value
			fun main() -> i32 {
				i32 x = 1
				plain(*x)
			}
			""",
			out var diagnostics);

		Assert.Null(module);
		Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("уберите маркер"));
	}
}
