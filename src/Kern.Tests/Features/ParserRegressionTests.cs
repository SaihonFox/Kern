using Kern.Tests.Features.Support;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>Регрессии парсера: две строки-выражения не должны считаться объявлением.</summary>
[Collection("timing")]
public sealed class ParserRegressionTests
{
	[Fact]
	public void Two_expression_lines_in_a_lambda_are_not_a_typed_local()
	{
		const string source = """
			fun make_counter() -> fun() -> i32 {
				i32 count = 0
				fun -> i32 {
					count += 1
					count
					count
				}
			}
			""";

		// Главное — компиляция без KRN2104/KRN1001; последняя строка тела — значение.
		StdPipeline.Compile(source);
	}
}
