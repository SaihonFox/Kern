using Kern.Bytecode;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Text;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.CodeGen;

/// <summary>
/// M4: таблица строк исходника в байткоде — границы операторов, путь файла,
/// обратная совместимость без <see cref="SourceText"/> и номер строки в панике VM.
/// </summary>
public sealed class LineTableCodegenTests
{
	private const string MultilineSource =
		"""
		fun f(i32 x) -> i32 {
			i32 y = 0
			y = x + 1
			if y > 0 {
				y = y * 2
			}
			y
		}
		""";

	[Fact]
	public void Statements_record_their_source_lines()
	{
		var result = Compile(MultilineSource, new SourceText(MultilineSource, "demo.kern"));

		Assert.Empty(result.Diagnostics);
		var function = Function(result.Module, "f");
		var lines = function.LineTable.Select(entry => entry.Line).ToList();

		Assert.Contains(3, lines);
		Assert.Contains(5, lines);
		Assert.True(function.LineTable.Count >= 2);

		for (var index = 1; index < function.LineTable.Count; index++)
		{
			Assert.True(function.LineTable[index].Offset > function.LineTable[index - 1].Offset);
			Assert.True(function.LineTable[index].Line > function.LineTable[index - 1].Line);
		}

		var lineFive = function.LineTable.Single(entry => entry.Line == 5);
		Assert.Equal(5, function.LineAt(lineFive.Offset));
		KbcModuleValidator.Validate(result.Module);
	}

	[Fact]
	public void Source_file_comes_from_the_source_path()
	{
		var withPath = Compile(MultilineSource, new SourceText(MultilineSource, "demo.kern"));
		Assert.Equal("demo.kern", Function(withPath.Module, "f").SourceFile);

		var withoutPath = Compile(MultilineSource, new SourceText(MultilineSource));
		Assert.Null(Function(withoutPath.Module, "f").SourceFile);
	}

	[Fact]
	public void Generate_without_source_keeps_the_line_table_empty()
	{
		var result = Compile(MultilineSource);
		var function = Function(result.Module, "f");

		Assert.Empty(function.LineTable);
		Assert.Null(function.SourceFile);
	}

	[Fact]
	public void Constructor_prologue_records_the_declaration_line()
	{
		const string source =
			"""
			unit Point(i32 x)

			fun f() -> i32 {
				Point p = Point(1)
				p.x
			}
			""";

		var result = Compile(source, new SourceText(source, "demo.kern"));
		var constructor = Function(result.Module, "ctor");

		Assert.Equal(0, constructor.LineTable[0].Offset);
		Assert.Equal(1, constructor.LineTable[0].Line);
		KbcModuleValidator.Validate(result.Module);
	}

	[Fact]
	public void Constructor_initializer_records_its_own_line()
	{
		const string source =
			"""
			type Box {
				i32 value = 7
			}

			fun f() -> i32 {
				Box box = Box()
				box.value
			}
			""";

		var result = Compile(source, new SourceText(source, "demo.kern"));
		var constructor = Function(result.Module, "ctor");

		Assert.Equal(0, constructor.LineTable[0].Offset);
		Assert.Equal(1, constructor.LineTable[0].Line);
		Assert.Contains(2, constructor.LineTable.Select(entry => entry.Line));
		KbcModuleValidator.Validate(result.Module);
	}

	[Fact]
	public void Panic_frame_reports_the_statement_line()
	{
		const string source =
			"""
			fun f(i32 x) -> i32 {
				i32 zero = 0
				i32 y = x / zero
				y
			}
			""";

		var result = Compile(source, new SourceText(source, "panic.kern"));
		var panic = Assert.Throws<KernPanic>(
			() => new Interpreter(result.Module).Run("f", KValue.I32(1)));

		Assert.Contains("строка 3", panic.Frames[0], StringComparison.Ordinal);
	}

	private static FunctionDef Function(KbcModule module, string name) =>
		Assert.Single(module.FunctionPool, function => function.Name == name);

	private static CodegenResult Compile(string source, SourceText? text = null)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		var bound = new Binder(module, table).Bind();
		Assert.DoesNotContain(bound.Diagnostics, diagnostic => diagnostic.IsError);
		return new CodeGenerator().Generate(bound, text);
	}
}
