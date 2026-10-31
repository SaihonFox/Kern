using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Native;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Batch 2 (M1–M3): cells (изменяемый захват), деструктуризация unit, объявление с
/// конструктором без <c>=</c>, вывод generic-конструктора, итерация dict/str и
/// loop-выражение со списком результатов.
/// </summary>
public sealed class BatchTwoTests
{
	[Fact]
	public void Cells_let_closures_mutate_captured_state()
	{
		Assert.Equal(KValue.I32(2), Run("""
			fun f() -> i32 {
				i32 n = 0
				fun() -> i32 inc = fun() -> i32 {
					n += 1
					n
				}
				inc()
				inc()
			}
			""", "f"));
	}

	[Fact]
	public void Unit_is_destructured_by_fields()
	{
		Assert.Equal(KValue.F64(3.0), Run("""
			unit Point(f64 x, f64 y)

			fun f() -> f64 {
				(f64 px, f64 py) = Point(1.0, 2.0)
				px + py
			}
			""", "f"));
	}

	[Fact]
	public void Constructor_declaration_without_equals()
	{
		Assert.Equal(KValue.I32(2), Run("""
			unit Point(i32 x, i32 y)

			fun f() -> i32 {
				Point a(1, 2)
				a.y
			}
			""", "f"));
	}

	[Fact]
	public void Generic_constructor_infers_type_arguments()
	{
		Assert.Equal(KValue.I32(5), Run("""
			unit Box<T>(T value)

			fun f() -> i32 {
				Box<i32> b = Box(5)
				b.value
			}
			""", "f"));
	}

	[Fact]
	public void Dict_and_str_are_iterable()
	{
		Assert.Equal(KValue.I32(2), Run("""
			fun f() -> i32 {
				dict<str, i32> d = ["a": 1, "b": 2]
				i32 keys = 0
				loop var k in d { keys += 1 }
				keys
			}
			""", "f"));
		Assert.Equal(KValue.I32(3), Run("""
			fun f() -> i32 {
				i32 chars = 0
				loop var ch in "abc" { chars += 1 }
				chars
			}
			""", "f"));
	}

	[Fact]
	public void Loop_expression_collects_values_into_a_list()
	{
		Assert.Equal(KValue.I32(9), Run("""
			fun f() -> i32 {
				list<i32> squares = loop i32 i in 1..=3 i * i
				squares[2]
			}
			""", "f"));
		Assert.Equal(KValue.I32(2), Run("""
			fun f() -> i32 {
				list<char> chars = loop var ch in "ab" ch
				chars.len
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
