using System.Text.RegularExpressions;
using Kern.Cli.Infrastructure;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Project;
using Kern.Core;
using Kern.Runtime.Macros;
using Kern.Vm.Execution;
using Kern.Vm.Reflection;
using Kern.Vm.Values;

namespace Kern.Cli.Macros;

/// <summary>Макрос, найденный в проекте: имя, функция и вид (`#[macro("expr")]` — вызов `имя!(…)`).</summary>
internal sealed record MacroInfo(string Name, MethodInfo Method, bool IsExpr);

/// <summary>
/// Раскрытие макросов до разбора проекта. Файлы с `#[macro]` компилируются отдельным мини-проектом и выполняются в VM;
/// затем в остальных файлах раскрываются `имя!(…)` и атрибутные макросы (`#[имя]`, `#[derive(a, b)]`): макрос возвращает
/// исходный текст Kern, который вставляется в файл. Файлы без макросов не затрагиваются.
/// Правило: файл с макросами может использовать только std, библиотеки и другие файлы с макросами.
/// </summary>
internal sealed partial class MacroEngine(INativeSymbolSource natives, IStdSource std, string? projectDir) : ISourceTransform
{
	private const int MaxPasses = 16;
	private static readonly Regex MacroMarker = new(@"#\[\s*macro\b", RegexOptions.Compiled);

	public IReadOnlyList<SourceText> Transform(string projectName, IReadOnlyList<SourceText> files, DiagnosticBag diagnostics)
	{
		var macroFiles = files.Where(f => MacroMarker.IsMatch(f.Text)).ToList();
		if (macroFiles.Count == 0) return files;
		var macros = Compile(projectName, macroFiles, diagnostics);
		if (macros is null) return files;

		var result = new List<SourceText>(files.Count);
		foreach (var f in files)
			result.Add(macroFiles.Contains(f) ? f : f with { Text = ExpandFile(f, macros, diagnostics) });
		return result;
	}

	/// <summary>Стадия 1: компиляция файлов с макросами и поиск функций `#[macro]`.</summary>
	private Dictionary<string, MacroInfo>? Compile(string projectName, List<SourceText> macroFiles, DiagnosticBag diagnostics)
	{
		var model = new ProjectLoader(natives, std).LoadFiles(projectName + ".macros", macroFiles);
		Kern.Compiler.Semantics.Bound.Declarations.BoundProgram? program = null;
		if (!model.HasErrors) program = SemanticAnalyzer.Analyze(model);
		if (model.HasErrors || program is null)
		{
			diagnostics.AddRange(model.Diagnostics.Where(d => d.Severity == Severity.Error));
			return null;
		}

		var module = CodeGenerator.Generate(program, projectName + ".macros", 0);
		var vm = Pipeline.CreateVm(module, projectDir);
		vm.Initialize();

		var table = new Dictionary<string, MacroInfo>();
		foreach (var m in Reflector.FunctionsWithAttribute(vm, "macro"))
		{
			if (m.Declaring is not null) continue;
			if (m.Parameters.Count != 1)
			{
				diagnostics.Error(Krn.MacroSignature, $"макрос '{m.Name}' должен принимать один параметр `macro_ctx m`", null, default);
				continue;
			}
			bool isExpr = m.GetAttribute("macro") is { Positional.Count: > 0 } a
				&& a.Positional[0].Kind == ValueKind.Str && a.Positional[0].AsStr == "expr";
			table[m.Name] = new MacroInfo(m.Name, m, isExpr);
		}
		return table;
	}

	private string ExpandFile(SourceText file, Dictionary<string, MacroInfo> macros, DiagnosticBag diagnostics) =>
		ExpandAttributes(file, ExpandExpressions(file, macros, diagnostics), macros, diagnostics);

	/// <summary>Вызывает макрос; возвращает текст, возвращённый функцией (`ret "…"`), и переносит сообщения в диагностику.</summary>
	private static string? Run(MacroInfo macro, MacroCtx ctx, SourceFile file, TextSpan span, DiagnosticBag diagnostics)
	{
		string? returned = null;
		bool failed = false;
		try
		{
			var r = macro.Method.Invoke(default, KValue.Obj(ctx));
			if (r.Kind == ValueKind.Str) returned = r.AsStr;
		}
		catch (KernPanic p)
		{
			diagnostics.Error(Krn.MacroPanic, $"макрос '{ctx.Name}' завершился паникой: {p.Message}", file, span);
			failed = true;
		}
		foreach (var (isError, message) in ctx.Messages)
		{
			diagnostics.Report(isError ? Severity.Error : Severity.Warning, isError ? Krn.MacroError : Krn.MacroWarning,
				$"макрос '{ctx.Name}': {message}", file, span);
			failed |= isError;
		}
		return failed ? null : returned;
	}

	private static string Src(string text, TextSpan span)
	{
		int start = Math.Clamp(span.Start, 0, text.Length);
		int end = Math.Clamp(span.End, start, text.Length);
		return text[start..end];
	}
}
