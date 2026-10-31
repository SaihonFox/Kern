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
/// Batch 1 (M1–M3): кортежи, `in`/`!in`, деструктуризация списков и кортежей, лямбды
/// с захватом по значению, расширения типов, значения по умолчанию и оператор повтора.
/// Набор написан после пяти языковых фич (правило заказчика 2026-10-31).
/// </summary>
public sealed class BatchOneTests
{
	[Fact]
	public void Tuple_literal_and_named_access()
	{
		Assert.Equal(KValue.I32(3), Run("""
			fun f() -> i32 {
				(i32 x, i32 y) p = (1, 2)
				p.x + p.y
			}
			""", "f"));
	}

	[Fact]
	public void Tuple_destructuring_into_typed_targets()
	{
		Assert.Equal(KValue.I32(10), Run("""
			fun f() -> i32 {
				(i32 a, str b) = (10, "hi")
				a
			}
			""", "f"));
	}

	[Fact]
	public void Membership_operator_for_list_dict_and_str()
	{
		Assert.Equal(KValue.Bool(true), Run("""
			fun f() -> bool {
				list<i32> xs = [1, 5, 9]
				5 in xs
			}
			""", "f"));
		Assert.Equal(KValue.Bool(true), Run("""
			fun f() -> bool {
				list<i32> xs = [1, 5, 9]
				3 !in xs
			}
			""", "f"));
		Assert.Equal(KValue.Bool(true), Run("""
			fun f() -> bool {
				dict<str, i32> d = ["a": 1]
				"a" in d
			}
			""", "f"));
		Assert.Equal(KValue.Bool(true), Run("""
			fun f() -> bool {
				"ell" in "hello" && 'h' in "hello"
			}
			""", "f"));
	}

	[Fact]
	public void List_destructuring_copies_the_rest()
	{
		Assert.Equal(KValue.I32(32), Run("""
			fun f() -> i32 {
				(i32 first, list<i32> ..rest, _) = [10, 20, 30, 40]
				first + rest.len + rest[0]
			}
			""", "f"));
	}

	[Fact]
	public void Lambda_without_and_with_capture()
	{
		Assert.Equal(KValue.I32(36), Run("""
			fun f() -> i32 {
				fun(i32) -> i32 sq = fun(i32 n) -> i32 n * n
				i32 k = 10
				fun(i32) -> i32 add = fun(i32 n) -> i32 n + k
				sq(5) + add(1)
			}
			""", "f"));
	}

	[Fact]
	public void Lambda_can_be_invoked_immediately()
	{
		Assert.Equal(KValue.I32(42), Run("""
			fun f() -> i32 {
				(fun(i32 v) -> i32 v * 2)(21)
			}
			""", "f"));
	}

	[Fact]
	public void Extension_function_property_and_operator()
	{
		Assert.Equal(KValue.Str("hey!"), Run("""
			fun str.shout() -> str { $ + "!" }
			fun f() -> str { "hey".shout() }
			""", "f"));

		Assert.Equal(KValue.I32(42), Run("""
			i32 i32.double
				$ * 2
			fun f() -> i32 { 21.double }
			""", "f"));

		Assert.Equal(KValue.I32(4), Run("""
			unit Point(i32 x, i32 y)
			op i32.*(Point p) -> Point { Point($ * p.x, $ * p.y) }
			fun f() -> i32 { (2 * Point(1, 2)).y }
			""", "f"));
	}

	[Fact]
	public void Default_parameter_value_is_used_when_omitted()
	{
		Assert.Equal(KValue.Str("привет, мир"), Run("""
			fun greet(str name = "мир") -> str { "привет, " + name }
			fun f() -> str { greet() }
			""", "f"));
		Assert.Equal(KValue.Str("привет, Bob"), Run("""
			fun greet(str name = "мир") -> str { "привет, " + name }
			fun f() -> str { greet("Bob") }
			""", "f"));
	}

	[Fact]
	public void Repeat_operator_for_str_and_char()
	{
		Assert.Equal(KValue.Str("HaHaHa"), Run("""fun f() -> str { 3 * "Ha" }""", "f"));
		Assert.Equal(KValue.Str("---"), Run("""fun f() -> str { '-' * 3 }""", "f"));
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
