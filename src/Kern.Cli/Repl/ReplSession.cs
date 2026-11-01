using System.Text;
using Kern.Binding.Catalog;
using Kern.Binding.Formatting;
using Kern.Binding.Resolution;
using Kern.Bytecode;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Native;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Cli.Repl;

/// <summary>
/// Состояние <c>kernc repl</c> (spec §16, раунд 6): объявления и присваивания накапливаются
/// и повторяются перед каждой строкой, поэтому переменные «переживают» ввод, хотя каждый
/// запуск компилирует свежую программу. Выражения печатаются через <see cref="KernFormat"/>.
/// </summary>
internal sealed class ReplSession
{
	private readonly List<string> _declarations = [];
	private readonly List<string> _state = [];

	public IReadOnlyList<string> Declarations => _declarations;

	/// <summary>Выполнить строку; ошибки печатаются в <paramref name="error"/> и не меняют состояние.</summary>
	public bool Evaluate(string line, TextWriter output, TextWriter error)
	{
		ArgumentNullException.ThrowIfNull(line);
		string text = line.Trim();
		if (text.Length == 0)
			return true;

		if (IsDeclaration(text))
		{
			_declarations.Add(text);
			if (!TryCompile(Program("0", expression: true), expression: true, out _, out string diagnostics))
			{
				_declarations.RemoveAt(_declarations.Count - 1);
				error.WriteLine(diagnostics);
				return false;
			}

			return true;
		}

		if (LooksStateful(text))
		{
			_state.Add(text);
			if (!TryCompile(Program("0", expression: true), expression: true, out _, out string diagnostics))
			{
				_state.RemoveAt(_state.Count - 1);
				error.WriteLine(diagnostics);
				return false;
			}

			return true;
		}

		string expressionProgram = Program(text, expression: true);
		if (TryCompile(expressionProgram, expression: true, out Compiled? compiled, out string expressionErrors))
		{
			KValue value = Run(compiled!);
			output.WriteLine(KernFormat.Value(value));
			return true;
		}

		string statementProgram = Program(text, expression: false);
		if (TryCompile(statementProgram, expression: false, out Compiled? statement, out string statementErrors))
		{
			_ = Run(statement!);
			return true;
		}

		error.WriteLine(expressionErrors.Length > 0 ? expressionErrors : statementErrors);
		return false;
	}

	private string Program(string body, bool expression)
	{
		var builder = new StringBuilder();
		foreach (string declaration in _declarations)
			builder.AppendLine(declaration);
		builder.Append(expression ? "fun __repl() -> object {" : "fun __repl() {");
		builder.AppendLine();
		foreach (string state in _state)
			builder.Append("\t").AppendLine(state);
		builder.Append('\t').AppendLine(body);
		builder.AppendLine("}");
		return builder.ToString();
	}

	private static bool IsDeclaration(string text) =>
		text.StartsWith("fun ", StringComparison.Ordinal) ||
		text.StartsWith("type ", StringComparison.Ordinal) ||
		text.StartsWith("unit", StringComparison.Ordinal) ||
		text.StartsWith("@type ", StringComparison.Ordinal) ||
		text.StartsWith("open type ", StringComparison.Ordinal) ||
		text.StartsWith("use ", StringComparison.Ordinal) ||
		text.StartsWith("project ", StringComparison.Ordinal);

	/// <summary>Строка меняет состояние: `var x = …`, `x = …`, `xs.add(…)` не считается.</summary>
	private static bool LooksStateful(string text)
	{
		if (text.StartsWith("var ", StringComparison.Ordinal))
			return true;
		if (!text.Contains('=', StringComparison.Ordinal))
			return false;
		if (text.Contains("==", StringComparison.Ordinal) || text.Contains("!=", StringComparison.Ordinal) ||
			text.Contains("<=", StringComparison.Ordinal) || text.Contains(">=", StringComparison.Ordinal) ||
			text.Contains("=>", StringComparison.Ordinal))
			return false;
		int quote = text.IndexOf('"');
		return quote < 0 || text.IndexOf('=', StringComparison.Ordinal) < quote;
	}

	private bool TryCompile(string source, bool expression, out Compiled? compiled, out string diagnostics)
	{
		compiled = null;
		var messages = new List<Diagnostic>();
		var parsed = Parser.Parse(source);
		messages.AddRange(parsed.Diagnostics);
		if (HasErrors(messages))
		{
			diagnostics = Describe(messages);
			return false;
		}

		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		messages.AddRange(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "repl");
		messages.AddRange(collectorDiagnostics);
		if (HasErrors(messages))
		{
			diagnostics = Describe(messages);
			return false;
		}

		var catalog = NativeCatalog.Scan(typeof(CoreModule).Assembly);
		ModuleSymbol imported = NativeSymbolBuilder.Import(module, catalog, out var importProblems);
		if (importProblems.Count > 0)
		{
			diagnostics = string.Join(Environment.NewLine, importProblems.Select(problem => $"ошибка: {problem}"));
			return false;
		}

		var bound = new Binder(imported, table).Bind();
		messages.AddRange(bound.Diagnostics);
		if (HasErrors(messages))
		{
			diagnostics = Describe(messages);
			return false;
		}

		var generated = new CodeGenerator().Generate(bound);
		messages.AddRange(generated.Diagnostics);
		if (HasErrors(messages))
		{
			diagnostics = Describe(messages);
			return false;
		}

		compiled = new Compiled(generated.Module, catalog, expression);
		diagnostics = string.Empty;
		return true;
	}

	private static KValue Run(Compiled compiled)
	{
		var interpreter = new Interpreter(compiled.Module, new NativeResolver(compiled.Catalog));
		return interpreter.Run("__repl", []);
	}

	private static string Describe(IReadOnlyList<Diagnostic> diagnostics) =>
		string.Join(
			Environment.NewLine,
			diagnostics
				.Where(diagnostic => diagnostic.IsError)
				.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

	private static bool HasErrors(IReadOnlyList<Diagnostic> diagnostics) =>
		diagnostics.Any(diagnostic => diagnostic.IsError);

	private sealed record Compiled(KbcModule Module, NativeCatalog Catalog, bool Expression);
}
