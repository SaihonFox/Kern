using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary><c>kern.num</c>: статистика, векторы/матрицы, интерполяция, интеграл/корень, FFT, complex.</summary>
[Collection("timing")]
public sealed class NumModuleTests
{
	[Fact]
	public void Statistics_and_regression()
	{
		const string source = """
			fun mean_value() -> f64 mean([1.0, 2.0, 3.0, 4.0])
			fun median_value() -> f64 median([3.0, 1.0, 2.0, 4.0])
			fun variance_value() -> f64 variance([1.0, 2.0, 3.0])
			fun correlation_value() -> f64 correlation([1.0, 2.0, 3.0], [2.0, 4.0, 6.0])
			fun slope() -> f64 regression([1.0, 2.0, 3.0], [3.0, 5.0, 7.0])[0]
			""";

		Assert.Equal(2.5, (double)StdPipeline.Run(source, "mean_value").AsF64);
		Assert.Equal(2.5, (double)StdPipeline.Run(source, "median_value").AsF64);
		Assert.Equal(1.0, (double)StdPipeline.Run(source, "variance_value").AsF64);
		Assert.Equal(1.0, (double)StdPipeline.Run(source, "correlation_value").AsF64);
		Assert.Equal(2.0, (double)StdPipeline.Run(source, "slope").AsF64);
	}

	[Fact]
	public void Matrices_and_vectors()
	{
		const string source = """
			fun product() -> f64 dot_product([1.0, 2.0], [3.0, 4.0])
			fun multiply() -> f64 matmul([[1.0, 2.0], [3.0, 4.0]], [[2.0], [1.0]])[0][0]
			fun solution() -> f64 solve([[2.0, 1.0], [1.0, 3.0]], [3.0, 5.0])[0]
			""";

		Assert.Equal(11.0, (double)StdPipeline.Run(source, "product").AsF64);
		Assert.Equal(4.0, (double)StdPipeline.Run(source, "multiply").AsF64);
		Assert.Equal(0.8, (double)StdPipeline.Run(source, "solution").AsF64);
	}

	[Fact]
	public void Interpolation_integration_and_roots()
	{
		const string source = """
			fun midpoint() -> f64 interpolate([0.0, 10.0], [0.0, 100.0], 5.0)
			fun square() -> f64 integrate(fun (f64 x) -> f64 x * x, 0.0, 3.0)
			fun root_two() -> f64 root(fun (f64 x) -> f64 x * x - 4.0, 0.0, 10.0)
			""";

		Assert.Equal(50.0, (double)StdPipeline.Run(source, "midpoint").AsF64);
		Assert.Equal(9.0, (double)StdPipeline.Run(source, "square").AsF64, 6);
		Assert.Equal(2.0, (double)StdPipeline.Run(source, "root_two").AsF64, 6);
	}

	[Fact]
	public void Fft_returns_interleaved_pairs()
	{
		const string source = """
			fun values() -> list<f64> fft([1.0, 0.0, 0.0, 0.0])
			""";

		var list = (Kern.Vm.Values.KList)StdPipeline.Run(source, "values").Ref!;
		Assert.Equal(1.0, list[0].AsF64);
		Assert.Equal(1.0, list[2].AsF64);
		Assert.Equal(1.0, list[4].AsF64);
		Assert.Equal(1.0, list[6].AsF64);
	}

	[Fact]
	public void Complex_numbers_use_operators()
	{
		const string source = """
			fun modulus() -> f64 complex(3.0, 4.0).abs()
			fun product() -> str (complex(1.0, 2.0) * complex(1.0, -2.0)).to_str()
			""";

		Assert.Equal(5.0, (double)StdPipeline.Run(source, "modulus").AsF64);
		Assert.Equal(KValue.Str("5+0i"), StdPipeline.Run(source, "product"));
	}
}
