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
using Kern.Core.Text;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.CodeGen;

/// <summary>
/// Мультифайловый модуль (M8): каждый файл разбирается отдельно, тексты склеиваются
/// <see cref="SourceBundle"/>, а таблица символов, сборщик объявлений и binder работают с
/// общим текстом без изменений. Codegen получает bundle и пишет на функцию путь её файла
/// и локальные строки файла.
/// </summary>
public sealed class MultiFileCodegenTests
{
	[Fact]
	public void Function_from_another_file_is_callable_and_survives_round_trip()
	{
		var (result, _) = Compile(
			("a.kern", "fun add(i32 a, i32 b) -> i32 {\n\ta + b\n}\n"),
			("b.kern", "fun combine() -> i32 {\n\tadd(20, 22)\n}\n"));

		Assert.Empty(result.Diagnostics);
		KbcModuleValidator.Validate(result.Module);

		var add = Function(result.Module, "add");
		var combine = Function(result.Module, "combine");
		Assert.Equal("a.kern", add.SourceFile);
		Assert.Equal("b.kern", combine.SourceFile);

		// Строки локальны файлу: тело combine — строка 2, а не строка 5 общего текста.
		Assert.Equal(2, Assert.Single(combine.LineTable).Line);
		Assert.Equal(2, Assert.Single(add.LineTable).Line);

		var bytes = KbcWriter.Write(result.Module);
		var loaded = KbcReader.Read(bytes);
		Assert.Equal(KValue.I32(42), new Interpreter(loaded).Run("combine"));
	}

	[Fact]
	public void Panic_in_the_second_file_reports_its_own_path_and_local_line()
	{
		var (result, _) = CompileWithNatives(
			("first.kern", "fun safe() -> i32 {\n\t1\n}\n"),
			("second.kern", "fun boom() {\n\tpanic(\"boom\")\n}\n"));

		Assert.Empty(result.Diagnostics);
		var boom = Function(result.Module, "boom");
		Assert.Equal("second.kern", boom.SourceFile);

		var interpreter = new Interpreter(result.Module, new NativeResolver(Catalog()));
		var panic = Assert.Throws<KernPanic>(() => interpreter.Run("boom"));

		Assert.Contains("строка 2", panic.Frames[0], StringComparison.Ordinal);
		Assert.DoesNotContain("строка 5", panic.Frames[0], StringComparison.Ordinal);
	}

	[Fact]
	public void Duplicate_top_level_name_across_files_reports_KRN1400_at_the_second_file()
	{
		var bundle = SourceBundle.Create(
		[
			("a.kern", "fun dup() -> i32 {\n\t1\n}\n"),
			("b.kern", "// второй файл\nfun dup() -> i32 {\n\t2\n}\n")
		]);

		var parsed = Parser.Parse(bundle.Text.Text);
		Assert.Empty(parsed.Diagnostics);
		var (_, diagnostics) = SymbolTable.Collect(parsed.Tree);

		var duplicate = Assert.Single(diagnostics);
		Assert.Equal("KRN1400", duplicate.Code);
		Assert.Equal("b.kern", bundle.PathAt(duplicate.Span.Start));

		var (path, line, column) = bundle.GetLineColumn(duplicate.Span.Start);
		Assert.Equal("b.kern", path);
		Assert.Equal(2, line);

		string formatted = DiagnosticText.Format(duplicate, bundle);
		Assert.Equal($"b.kern:2:{column}: ошибка KRN1400: {duplicate.Message}", formatted);
		Assert.Contains("dup", formatted, StringComparison.Ordinal);

		// В общей склейке это была бы строка 6 — формат обязан показать локальную.
		Assert.Equal(6, bundle.Text.GetLine(duplicate.Span.Start));
	}

	[Fact]
	public void Ctors_and_methods_use_their_own_file_and_local_lines()
	{
		var (result, _) = Compile(
			("a.kern", "type Alpha {\n\ti32 x\n}\n"),
			("b.kern", "type Beta {\n\ti32 y\n}\n\nfun make() -> i32 {\n\tBeta b = Beta()\n\tb.y\n}\n"));

		Assert.Empty(result.Diagnostics);
		var ctors = result.Module.FunctionPool.Where(function => function.Name == "ctor").ToList();
		Assert.Equal(2, ctors.Count);

		var alphaCtor = Assert.Single(ctors, function => result.Module.TypePool[function.OwnerType].Name == "Alpha");
		var betaCtor = Assert.Single(ctors, function => result.Module.TypePool[function.OwnerType].Name == "Beta");
		Assert.Equal("a.kern", alphaCtor.SourceFile);
		Assert.Equal("b.kern", betaCtor.SourceFile);

		// Синтезированный ctor Beta объявлен в первом строке своего файла, а не в общей склейке.
		Assert.Equal(1, Assert.Single(betaCtor.LineTable).Line);

		var make = Function(result.Module, "make");
		Assert.Equal("b.kern", make.SourceFile);
		Assert.Equal(6, make.LineTable[0].Line);
	}

	private static SourceBundle Bundle(params (string Path, string Text)[] files) =>
		SourceBundle.Create(files);

	private static (CodegenResult Result, SourceBundle Bundle) Compile(
		params (string Path, string Text)[] files) =>
		CompileCore(importNatives: false, files);

	private static (CodegenResult Result, SourceBundle Bundle) CompileWithNatives(
		params (string Path, string Text)[] files) =>
		CompileCore(importNatives: true, files);

	private static (CodegenResult Result, SourceBundle Bundle) CompileCore(
		bool importNatives,
		(string Path, string Text)[] files)
	{
		var bundle = Bundle(files);

		// Каждый файл обязан разбираться и сам по себе: склейка не должна «спасать» синтаксис.
		foreach (var (_, text) in files)
			Assert.Empty(Parser.Parse(text).Diagnostics);

		var parsed = Parser.Parse(bundle.Text.Text);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);

		if (importNatives)
		{
			module = NativeSymbolBuilder.Import(module, Catalog(), out var importDiagnostics);
			Assert.Empty(importDiagnostics);
		}

		var bound = new Binder(module, table).Bind();
		Assert.DoesNotContain(bound.Diagnostics, diagnostic => diagnostic.IsError);

		var generated = new CodeGenerator().Generate(bound, bundle);
		Assert.DoesNotContain(generated.Diagnostics, diagnostic => diagnostic.IsError);
		return (generated, bundle);
	}

	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	private static FunctionDef Function(KbcModule module, string name) =>
		Assert.Single(module.FunctionPool, function => function.Name == name);
}
