using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Native;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Execution;

/// <summary>
/// Итерационный <c>loop … in …</c> (spec §3): диапазон <c>a..b</c>/<c>a..=b</c> с <c>:step</c>,
/// <c>list</c>/<c>array</c>, тип переменной цикла (в т.ч. преобразование), <c>stop</c>/<c>next</c>.
/// </summary>
public sealed class IterationLoopTests
{
	[Fact]
	public void Range_loop_sums_values()
	{
		Assert.Equal(KValue.I32(15), Run("""
			fun f() -> i32 {
				i32 sum = 0
				loop i32 i in 1..6 {
					sum += i
				}
				sum
			}
			""", "f"));
	}

	[Fact]
	public void Inclusive_bounds_and_step()
	{
		Assert.Equal(KValue.I32(6), Run("""
			fun f() -> i32 {
				i32 sum = 0
				loop var i in 1..=3 {
					sum += i
				}
				sum
			}
			""", "f"));
		Assert.Equal(KValue.I32(18), Run("""
			fun f() -> i32 {
				i32 sum = 0
				loop var i in 0..10:3 {
					sum += i
				}
				sum
			}
			""", "f"));
	}

	[Fact]
	public void List_and_array_are_iterated_in_order()
	{
		Assert.Equal(KValue.I32(6), Run("""
			fun f() -> i32 {
				i32 sum = 0
				loop var v in [1, 2, 3] {
					sum += v
				}
				sum
			}
			""", "f"));
		Assert.Equal(KValue.I32(6), Run("""
			fun f() -> i32 {
				array<i32> xs = <i32>[1, 2, 3]
				i32 sum = 0
				loop var v in xs {
					sum += v
				}
				sum
			}
			""", "f"));
	}

	[Fact]
	public void Stop_and_next_work_inside_the_body()
	{
		Assert.Equal(KValue.I32(12), Run("""
			fun f() -> i32 {
				i32 sum = 0
				loop var i in 1..10 {
					if i == 3 {
						next
					}
					if i == 6 {
						stop
					}
					sum += i
				}
				sum
			}
			""", "f"));
	}

	[Fact]
	public void Declared_loop_type_is_converted()
	{
		Assert.Equal(KValue.F64(3.0), Run("""
			fun f() -> f64 {
				f64 sum = 0.0
				loop f64 x in 1..3 {
					sum += x
				}
				sum
			}
			""", "f"));
	}

	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	private static KValue Run(string source, string function) =>
		new Interpreter(Compile(source).Module, new NativeResolver(Catalog())).Run(function, []);

	private static CodegenResult Compile(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);

		var catalog = Catalog();
		ModuleSymbol imported = NativeSymbolBuilder.Import(module, catalog, out var importDiagnostics);
		Assert.Empty(importDiagnostics);

		var bound = new Binder(imported, table).Bind();
		Assert.DoesNotContain(bound.Diagnostics, diagnostic => diagnostic.IsError);
		var generated = new CodeGenerator().Generate(bound);
		Assert.DoesNotContain(generated.Diagnostics, diagnostic => diagnostic.IsError);
		return generated;
	}
}
