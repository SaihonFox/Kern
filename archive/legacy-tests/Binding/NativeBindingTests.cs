using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Bytecode;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Native;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Binding;

public sealed class NativeBindingTests
{
	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	[Fact]
	public void Catalog_scans_prelude_without_diagnostics()
	{
		NativeCatalog catalog = Catalog();
		File.WriteAllLines(
			@"C:\Users\ILNAR\Desktop\KernDS\logs\native-catalog.txt",
			catalog.Functions.Select(function => function.NativeKey)
				.Concat(catalog.Diagnostics.Select(diagnostic => "DIAG: " + diagnostic)));

		Assert.Empty(catalog.Diagnostics);
		Assert.Contains(catalog.Modules, module => module.Name == "kern.core");
		Assert.Contains(catalog.Modules, module => module.Name == "kern.math");
		Assert.Contains(catalog.Modules, module => module.Name == "kern.io");
		Assert.Contains(catalog.Types, type => type.Name == "str" && type.IsBuiltin);
		Assert.Contains(catalog.Types, type => type.Name == "list" && type.IsBuiltin);
		Assert.Contains(catalog.Functions, function => function.NativeKey == "kern.core|println|m|1|object");
		Assert.Contains(catalog.Functions, function => function.NativeKey == "str|len|g|1|str");
		Assert.Contains(catalog.Functions, function => function.NativeKey == "list|add|m|1|list<object>,object");
	}

	[Fact]
	public void Resolver_calls_format_and_str_property()
	{
		NativeCatalog catalog = Catalog();
		NativeResolver resolver = new(catalog);
		Interpreter interpreter = new(new KbcModule { Name = "native-test" });

		Assert.True(resolver.TryResolve("kern.core|format|m|1|object", out var format));
		Assert.Equal("42", format.Invoke(interpreter, [KValue.I32(42)], []).AsStr);

		Assert.True(resolver.TryResolve("str|len|g|1|str", out var len));
		Assert.Equal(KValue.I32(6), len.Invoke(interpreter, [KValue.Str("Привет")], []));

		Assert.True(resolver.TryResolve("str|upper|m|1|str", out var upper));
		Assert.Equal("ПРИВЕТ", upper.Invoke(interpreter, [KValue.Str("Привет")], []).AsStr);
	}

	[Fact]
	public void Resolver_calls_list_members()
	{
		NativeCatalog catalog = Catalog();
		NativeResolver resolver = new(catalog);
		Interpreter interpreter = new(new KbcModule { Name = "native-test" });
		KList list = new(null, [KValue.I32(1), KValue.I32(2)]);
		KValue receiver = KValue.Obj(list);

		Assert.True(resolver.TryResolve("list|add|m|1|list<object>,object", out var add));
		add.Invoke(interpreter, [receiver, KValue.I32(3)], []);
		Assert.Equal(3, list.Count);

		Assert.True(resolver.TryResolve("list|len|g|1|list<object>", out var len));
		Assert.Equal(KValue.I32(3), len.Invoke(interpreter, [receiver], []));
	}

	[Fact]
	public void Compiler_runs_top_level_native_and_str_member()
	{
		CodegenResult result = Compile(
			"""
			fun f -> i32 {
				str s = "Привет"
				i32 n = s.len
				i32 m = abs(-5)
				n + m
			}
			""");

		KValue value = new Interpreter(result.Module, new NativeResolver(Catalog())).Run("f", []);

		Assert.Equal(KValue.I32(11), value);
	}

	[Fact]
	public void Debug_len_and_abs()
	{
		CodegenResult len = Compile(
			"""
			fun f -> i32 {
				str s = "Привет"
				s.len
			}
			""");
		Assert.Equal(
			KValue.I32(6),
			new Interpreter(len.Module, new NativeResolver(Catalog())).Run("f", []));

		CodegenResult abs = Compile(
			"""
			fun f -> i32 {
				abs(-5)
			}
			""");
		Assert.Equal(
			KValue.I32(5),
			new Interpreter(abs.Module, new NativeResolver(Catalog())).Run("f", []));
	}

	[Fact]
	public void Compiler_runs_native_list_member_on_parameter()
	{
		CodegenResult result = Compile(
			"""
			fun f(list<i32> xs) -> i32 {
				xs.add(7)
				xs.len
			}
			""");

		KList list = new(null, [KValue.I32(1), KValue.I32(2)]);
		KValue value = new Interpreter(result.Module, new NativeResolver(Catalog()))
			.Run("f", [KValue.Obj(list)]);

		Assert.Equal(KValue.I32(3), value);
		Assert.Equal(3, list.Count);
	}

	[Fact]
	public void Compiler_calls_zero_argument_println()
	{
		CodegenResult result = Compile(
			"""
			fun main {
				println()
			}
			""");

		Assert.Empty(result.Diagnostics);
		KValue value = new Interpreter(result.Module, new NativeResolver(Catalog())).Run("main", []);
		Assert.True(value.IsVoid);
	}

	private static CodegenResult Compile(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);

		NativeCatalog catalog = Catalog();
		ModuleSymbol imported = NativeSymbolBuilder.Import(module, catalog, out var importDiagnostics);
		Assert.Empty(importDiagnostics);
		File.WriteAllLines(
			@"C:\Users\ILNAR\Desktop\KernDS\logs\native-imported.txt",
			imported.Functions.Values.SelectMany(group => group)
				.Select(function => function.Signature + "  native=" + function.IsNative + " params=" +
					string.Join(";", function.Parameters.Select(parameter => parameter.Name + ":" + parameter.Type))));

		var bound = new Binder(imported, table).Bind();
		Assert.Empty(bound.Diagnostics);
		var generated = new CodeGenerator().Generate(bound);
		Assert.Empty(generated.Diagnostics);
		return generated;
	}
}
