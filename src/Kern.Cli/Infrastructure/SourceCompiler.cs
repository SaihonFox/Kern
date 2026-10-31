using Kern.Binding.Catalog;
using Kern.Bytecode;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Native;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Kern.Core.Text;
using Kern.Runtime.Core;

namespace Kern.Cli.Infrastructure;

/// <summary>
/// Общий шаг компиляции <c>kernc run</c>, <c>build</c> и <c>check</c>: lexer → parser → таблица
/// символов → декларации → импорт нативов → binder → codegen → проверка модуля.
/// Один файл — отдельный модуль с именем файла без расширения; папка проекта — один модуль
/// с именем папки, склеенный из всех файлов через <see cref="SourceBundle"/>.
/// </summary>
internal static class SourceCompiler
{
	public static CompileResult Compile(string path)
	{
		string text;
		try
		{
			text = File.ReadAllText(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return new CompileResult { FailureMessage = $"не удалось прочитать {path}: {ex.Message}" };
		}

		var source = new SourceText(text, path);
		return CompileCore(source, text, ModuleName(path), bundle: null);
	}

	public static CompileResult Compile(ProjectSources project)
	{
		ArgumentNullException.ThrowIfNull(project);
		var bundle = SourceBundle.Create(project.Files);
		return CompileCore(bundle.Text, bundle.Text.Text, project.Name, bundle);
	}

	/// <summary>Компилирует вход команды: файл как есть или проект по соглашению.</summary>
	public static CompileResult Compile(string path, SourceKind kind)
	{
		if (kind != SourceKind.Project)
			return Compile(path);

		if (!ProjectSources.TryLoad(path, out ProjectSources? project, out string? error))
			return new CompileResult { FailureMessage = error };

		return Compile(project!);
	}

	private static CompileResult CompileCore(SourceText source, string text, string moduleName, SourceBundle? bundle)
	{
		var parsed = Parser.Parse(text);
		List<Diagnostic> diagnostics = [.. parsed.Diagnostics];
		if (HasError(diagnostics))
			return Failed(source, bundle, diagnostics);

		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		diagnostics.AddRange(tableDiagnostics);
		if (HasError(diagnostics))
			return Failed(source, bundle, diagnostics);

		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, moduleName);
		diagnostics.AddRange(collectorDiagnostics);
		if (HasError(diagnostics))
			return Failed(source, bundle, diagnostics);

		NativeCatalog catalog = NativeCatalog.Scan(typeof(CoreModule).Assembly);
		ModuleSymbol imported = NativeSymbolBuilder.Import(module, catalog, out var nativeProblems);
		if (nativeProblems.Count > 0)
			return new CompileResult
			{
				Source = source,
				Sources = bundle,
				Diagnostics = diagnostics,
				NativeProblems = nativeProblems,
			};

		var bound = new Binder(imported, table).Bind();
		diagnostics.AddRange(bound.Diagnostics);
		if (HasError(diagnostics))
			return Failed(source, bundle, diagnostics);

		var generated = bundle is null
			? new CodeGenerator().Generate(bound, source)
			: new CodeGenerator().Generate(bound, bundle);
		diagnostics.AddRange(generated.Diagnostics);
		if (HasError(diagnostics))
			return Failed(source, bundle, diagnostics);

		try
		{
			KbcModuleValidator.Validate(generated.Module);
		}
		catch (KbcFormatException ex)
		{
			return new CompileResult
			{
				Source = source,
				Sources = bundle,
				Diagnostics = diagnostics,
				FailureMessage = $"внутренняя ошибка компилятора: {ex.Message}",
			};
		}

		return new CompileResult
		{
			Source = source,
			Sources = bundle,
			Diagnostics = diagnostics,
			Program = new CompiledProgram(generated.Module, catalog),
		};
	}

	/// <summary>Печатает все диагностики (и предупреждения) в stderr в формате <c>путь:строка:колонка</c>.</summary>
	public static void Report(CompileResult result, TextWriter stderr)
	{
		ArgumentNullException.ThrowIfNull(result);
		ArgumentNullException.ThrowIfNull(stderr);

		if (result.FailureMessage is { } failure)
			stderr.WriteLine($"ошибка: {failure}");

		foreach (Diagnostic diagnostic in result.Diagnostics)
			stderr.WriteLine(result.Sources is { } bundle
				? DiagnosticText.Format(diagnostic, bundle)
				: Format(result.Source, diagnostic));

		foreach (string problem in result.NativeProblems)
			stderr.WriteLine($"ошибка: {problem}");
	}

	private static string Format(SourceText? source, Diagnostic diagnostic)
	{
		string kind = diagnostic.IsError ? "ошибка" : "предупреждение";
		if (source is null)
			return $"{kind} {diagnostic.Code}: {diagnostic.Message}";

		(int line, int column) = source.GetLineColumn(diagnostic.Span.Start);
		return $"{source.Path}:{line}:{column}: {kind} {diagnostic.Code}: {diagnostic.Message}";
	}

	private static bool HasError(IEnumerable<Diagnostic> diagnostics) =>
		diagnostics.Any(diagnostic => diagnostic.IsError);

	private static CompileResult Failed(
		SourceText source,
		SourceBundle? bundle,
		IReadOnlyList<Diagnostic> diagnostics) =>
		new() { Source = source, Sources = bundle, Diagnostics = diagnostics };

	/// <summary>Имя модуля для одного файла: имя файла без расширения (пустое — «main»).</summary>
	private static string ModuleName(string path)
	{
		string name = Path.GetFileNameWithoutExtension(path);
		return string.IsNullOrWhiteSpace(name) ? "main" : name;
	}
}
