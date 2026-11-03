using Kern.Tests.Features.Support;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Async-генераторы решено не поддерживать: асинхронные источники оформляются
/// через <c>async_iter&lt;T&gt;</c> (spec §6/§16).
/// </summary>
public sealed class AsyncGeneratorTests
{
	[Fact]
	public void Async_function_with_yield_is_rejected_with_a_hint()
	{
		var module = StdPipeline.CompileOrNull(
			"""
			async fun numbers() -> seq<i32> {
				yield 1
			}
			""",
			out var diagnostics);

		Assert.Null(module);
		Assert.Contains(diagnostics, diagnostic =>
			diagnostic.Message.Contains("async-генераторы") &&
			diagnostic.Message.Contains("async_iter"));
	}

	[Fact]
	public void Async_lambda_with_yield_is_rejected()
	{
		var module = StdPipeline.CompileOrNull(
			"""
			fun make() -> fun() -> seq<i32> {
				async fun gen() -> seq<i32> {
					yield 1
				}
				gen
			}
			""",
			out var diagnostics);

		Assert.Null(module);
		Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("async-генераторы"));
	}
}
