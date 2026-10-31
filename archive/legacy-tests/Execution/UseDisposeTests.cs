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
/// use/dispose (spec §12): <c>use T x = expr</c> освобождает переменную методом
/// <c>dispose()</c> при выходе из блока — по обычному завершению, <c>ret</c>,
/// <c>stop</c> и <c>next</c>; при панике очистка не выполняется. Тип без
/// <c>dispose()</c> без параметров — <c>KRN2101</c>.
/// </summary>
public sealed class UseDisposeTests
{
	[Fact]
	public void Dispose_runs_at_block_end_in_reverse_order()
	{
		const string source = """
			type Log {
				#i32 count = 0
				#i32 last = 0
			}

			type First {
				fun dispose() {
					Log.count = Log.count + 1
					Log.last = 1
				}
			}

			type Second {
				fun dispose() {
					Log.count = Log.count + 1
					Log.last = 2
				}
			}

			fun f() -> i32 {
				{
					use First a = First()
					use Second b = Second()
				}
				Log.count * 10 + Log.last
			}
			""";

		// count = 2, last = 1: Second освобождён первым, First — последним.
		Assert.Equal(KValue.I32(21), Run(source, "f"));
	}

	[Fact]
	public void Return_disposes_before_leaving_the_function()
	{
		const string source = """
			type Log {
				#i32 count = 0
			}

			type Res {
				fun dispose() {
					Log.count = Log.count + 1
				}
			}

			fun f() -> i32 {
				use var r = Res()
				ret 42
				0
			}

			fun count() -> i32 { Log.count }
			""";

		var interpreter = new Interpreter(Compile(source).Module, new NativeResolver(Catalog()));
		Assert.Equal(KValue.I32(42), interpreter.Run("f", []));
		Assert.Equal(KValue.I32(1), interpreter.Run("count", []));
	}

	[Fact]
	public void Stop_and_next_dispose_before_jumping()
	{
		const string source = """
			type Log {
				#i32 count = 0
			}

			type Res {
				fun dispose() {
					Log.count = Log.count + 1
				}
			}

			fun f() -> i32 {
				i32 k = 0
				loop k < 4 {
					use var r = Res()
					k += 1
					if k == 2 {
						next
					}
					if k == 3 {
						stop
					}
				}
				Log.count
			}
			""";

		// Три итерации: обычный конец, next и stop — каждая освобождает по разу.
		Assert.Equal(KValue.I32(3), Run(source, "f"));
	}

	[Fact]
	public void Panic_does_not_dispose()
	{
		const string source = """
			type Log {
				#i32 count = 0
			}

			type Res {
				fun dispose() {
					Log.count = Log.count + 1
				}
			}

			fun boom() {
				use var r = Res()
				panic("x")
			}

			fun count() -> i32 { Log.count }
			""";

		var interpreter = new Interpreter(Compile(source).Module, new NativeResolver(Catalog()));
		Assert.Throws<KernPanic>(() => interpreter.Run("boom", []));
		Assert.Equal(KValue.I32(0), interpreter.Run("count", []));
	}

	[Fact]
	public void Stop_from_a_nested_block_disposes_exactly_once()
	{
		const string source = """
			type Log {
				#i32 count = 0
			}

			type Res {
				fun dispose() {
					Log.count = Log.count + 1
				}
			}

			fun f() -> i32 {
				i32 k = 0
				loop k < 2 {
					k += 1
					{
						use var r = Res()
						if k == 1 {
							stop
						}
					}
				}
				Log.count
			}
			""";

		// stop выходит сразу из внутреннего блока и цикла: ровно один dispose.
		Assert.Equal(KValue.I32(1), Run(source, "f"));
	}

	[Fact]
	public void Type_without_dispose_reports_KRN2101()
	{
		var diagnostic = Assert.Single(
			Diagnostics("""
				type Plain(i32 x)

				fun f() {
					use var p = Plain(1)
				}
				"""),
			item => item.Code == "KRN2101");

		Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
		Assert.Contains("dispose", diagnostic.Message);
	}

	[Fact]
	public void Use_with_declared_type_disposes()
	{
		const string source = """
			type Log {
				#i32 count = 0
			}

			type Res {
				fun dispose() {
					Log.count = Log.count + 1
				}
			}

			fun f() -> i32 {
				use Res r = Res()
				Log.count
			}
			""";

		// Переменная ещё жива внутри блока: до выхода dispose не вызывался.
		Assert.Equal(KValue.I32(0), Run(source, "f"));
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

	private static IReadOnlyList<Diagnostic> Diagnostics(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		return new Binder(module, table).Bind().Diagnostics;
	}
}
