using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Bytecode;
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

namespace Kern.Tests.Features;

/// <summary>
/// M3, заготовки рефлексии времени компиляции (spec §17/§18): <c>typeof&lt;T&gt;()</c>
/// (реифицированное значение <c>type</c>, статически <c>object</c>) и
/// <c>name_of&lt;T&gt;()</c> (биндер вычисляет строку, в байткоде остаётся литерал).
/// Форма значения <c>typeof(x)</c> и рантайм-члены <c>type</c> в этот слайс не входят.
/// </summary>
public sealed class ReflectionStubTests
{
	[Fact]
	public void Typeof_pushes_the_reified_type()
	{
		Assert.Equal(KValue.Str("i32"), Run("""
			fun f() -> str { format(typeof<i32>()) }
			""", "f"));
		Assert.Equal(KValue.Str("list<i32>"), Run("""
			fun f() -> str { format(typeof<list<i32>>()) }
			""", "f"));
	}

	[Fact]
	public void Name_of_is_computed_at_compile_time()
	{
		const string source = """
			unit User(i32 id)
			fun type_name() -> str { name_of<User>() }
			""";
		Assert.Equal(KValue.Str("User"), Run(source, "type_name"));
		Assert.Equal(KValue.Str("list<i32>"), Run("fun f() -> str { name_of<list<i32>>() }", "f"));
		Assert.Equal(KValue.Str("i32"), Run("fun f() -> str { name_of<i32>() }", "f"));

		// Spec §18: в байткоде остаётся только строковый литерал — ни вызова, ни TypeOfSig.
		var compiled = Compile(source);
		var function = Assert.Single(compiled.Module.FunctionPool, item => item.Name == "type_name");
		Assert.Equal(new[] { OpCode.PushConst, OpCode.Ret }, OpCodes(function.Code));
	}

	[Fact]
	public void Meta_builtins_reject_wrong_use_and_yield_to_user_functions()
	{
		Assert.Equal(new[] { "KRN2101" }, Codes("fun main { println(typeof<i32, str>()) }"));
		Assert.Equal(new[] { "KRN2101" }, Codes("fun main { println(typeof<i32>(1)) }"));
		Assert.Equal(new[] { "KRN2101" }, Codes("fun main { println(typeof(1)) }"));
		Assert.Equal(new[] { "KRN2101" }, Codes("fun main { println(typeof()) }"));
		Assert.Equal(new[] { "KRN2101" }, Codes("fun main { println(name_of<i32, bool>()) }"));

		// Без аргументов-типов видимая пользовательская функция важнее контекстной формы.
		Assert.Equal(KValue.Str("user"), Run("""
			fun typeof(i32 value) -> str { "user" }

			fun f() -> str { typeof(1) }
			""", "f"));
	}

	private sealed record Outcome(CodegenResult? Code, IReadOnlyList<Diagnostic> Diagnostics);

	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	private static KValue Run(string source, string function) =>
		new Interpreter(Compile(source).Module, new NativeResolver(Catalog())).Run(function, []);

	private static CodegenResult Compile(string source)
	{
		var outcome = CompileWithDiagnostics(source);
		Assert.DoesNotContain(outcome.Diagnostics, diagnostic => diagnostic.IsError);
		Assert.NotNull(outcome.Code);
		return outcome.Code!;
	}

	/// <summary>Коды ошибок без codegen: каскад после первой ошибки не нужен.</summary>
	private static string[] Codes(string source) =>
		[.. CompileWithDiagnostics(source).Diagnostics.Where(diagnostic => diagnostic.IsError).Select(diagnostic => diagnostic.Code)];

	/// <summary>Полный конвейер одного файла с сохранением диагностик.</summary>
	private static Outcome CompileWithDiagnostics(string source)
	{
		var diagnostics = new List<Diagnostic>();
		var parsed = Parser.Parse(source);
		diagnostics.AddRange(parsed.Diagnostics);
		if (HasErrors(diagnostics))
			return new Outcome(null, diagnostics);

		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		diagnostics.AddRange(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		diagnostics.AddRange(collectorDiagnostics);

		var catalog = Catalog();
		ModuleSymbol imported = NativeSymbolBuilder.Import(module, catalog, out var importProblems);
		Assert.Empty(importProblems);

		var bound = new Binder(imported, table).Bind();
		diagnostics.AddRange(bound.Diagnostics);
		if (HasErrors(diagnostics))
			return new Outcome(null, diagnostics);

		var generated = new CodeGenerator().Generate(bound);
		diagnostics.AddRange(generated.Diagnostics);
		return HasErrors(diagnostics)
			? new Outcome(null, diagnostics)
			: new Outcome(generated, diagnostics);
	}

	private static bool HasErrors(IReadOnlyList<Diagnostic> diagnostics) =>
		diagnostics.Any(diagnostic => diagnostic.IsError);

	/// <summary>Декодирует код функции в список опкодов (для проверки «литерала в байткоде»).</summary>
	private static List<OpCode> OpCodes(int[] code)
	{
		var result = new List<OpCode>();
		for (var offset = 0; offset < code.Length;)
		{
			var op = (OpCode)code[offset];
			result.Add(op);
			offset += 1 + OpCodeInfo.OperandCount(op);
		}

		return result;
	}
}
