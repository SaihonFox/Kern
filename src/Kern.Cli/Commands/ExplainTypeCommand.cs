using Kern.Binding.Catalog;
using Kern.Compiler.Native;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Runtime.Core;

namespace Kern.Cli.Commands;

/// <summary>
/// <c>kernc explain-type &lt;файл.kern&gt; "&lt;выражение&gt;"</c>: показывает тип выражения,
/// выбранную перегрузку и выведенные generic-аргументы. Выражение компилируется внутри
/// того же модуля (в синтетической функции), поэтому видны и локальные объявления файла.
/// </summary>
internal static class ExplainTypeCommand
{
	private const string Probe = "__kern_explain_type__";

	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		if (args.Length != 2)
		{
			stderr.WriteLine("ошибка: использование: kernc explain-type <файл.kern> \"<выражение>\"");
			return 2;
		}

		string path = args[0];
		string expression = args[1];
		if (!File.Exists(path) || !path.EndsWith(".kern", StringComparison.OrdinalIgnoreCase))
		{
			stderr.WriteLine($"ошибка: файл .kern не найден: {path}");
			return 2;
		}

		string source;
		try
		{
			source = File.ReadAllText(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			stderr.WriteLine($"ошибка: не удалось прочитать {path}: {exception.Message}");
			return 1;
		}

		string text = source + $"\n\nfun {Probe}() {{\n\t{expression}\n}}\n";
		var parsed = Parser.Parse(text);
		if (parsed.Diagnostics.Any(diagnostic => diagnostic.IsError))
		{
			foreach (var diagnostic in parsed.Diagnostics)
				stderr.WriteLine(Describe(string.Empty, diagnostic.Code, diagnostic.Message, diagnostic.Span));
			return 1;
		}

		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		if (tableDiagnostics.Any(diagnostic => diagnostic.IsError))
		{
			foreach (var diagnostic in tableDiagnostics)
				stderr.WriteLine(Describe(string.Empty, diagnostic.Code, diagnostic.Message, diagnostic.Span));
			return 1;
		}

		NativeCatalog catalog = NativeCatalog.Scan(typeof(CoreModule).Assembly);
		var nativeTypes = NativeSymbolBuilder.CreateTypeSymbols(catalog);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(
			parsed.Tree, table, "explain", primes: null, external: null, nativeTypes);
		if (collectorDiagnostics.Any(diagnostic => diagnostic.IsError))
		{
			foreach (var diagnostic in collectorDiagnostics)
				stderr.WriteLine(Describe(string.Empty, diagnostic.Code, diagnostic.Message, diagnostic.Span));
			return 1;
		}

		ModuleSymbol imported = NativeSymbolBuilder.Import(module, catalog, out var problems);
		if (problems.Count > 0)
		{
			foreach (string problem in problems)
				stderr.WriteLine($"ошибка: {problem}");
			return 1;
		}

		var bound = new Binder(imported, table).Bind();
		var errors = bound.Diagnostics.Where(diagnostic => diagnostic.IsError).ToList();
		if (errors.Count > 0)
		{
			foreach (var diagnostic in errors)
				stderr.WriteLine(Describe(string.Empty, diagnostic.Code, diagnostic.Message, diagnostic.Span));
			return 1;
		}

		BoundFunction? probe = bound.FindFunction(Probe);
		BoundExpression? value = probe?.Body.Value ?? probe?.Body.Statements
			.OfType<BoundExpressionStatement>()
			.LastOrDefault()?.Expression;
		if (value is null)
		{
			stderr.WriteLine("ошибка: не удалось вывести выражение");
			return 1;
		}

		stdout.WriteLine($"выражение: {expression}");
		stdout.WriteLine($"тип: {value.Type}");
		ExplainCalls(value, stdout, depth: 0);
		return 0;
	}

	/// <summary>Печать вызовов внутри выражения: перегрузка и выведенные аргументы.</summary>
	private static void ExplainCalls(BoundExpression expression, TextWriter output, int depth)
	{
		string indent = new(' ', depth * 2);
		switch (expression)
		{
			case BoundCallExpression call:
				string owner = call.Function.Owner is { } type ? type.Name + "." : "";
				output.WriteLine($"{indent}вызов: {owner}{call.Function.Name}(" +
					string.Join(", ", call.Function.Parameters.Select(parameter => parameter.Type)) + ")" +
					(call.Function.ReturnType is { } result ? $" -> {result}" : ""));
				if (call.TypeArguments.Count > 0)
					output.WriteLine($"{indent}  generic: " + string.Join(", ",
						call.TypeArguments.Select((argument, index) => $"{ParameterName(call, index)}={argument}")));
				foreach (BoundExpression argument in call.Arguments)
					ExplainCalls(argument, output, depth + 1);
				if (call.Receiver is not null)
					ExplainCalls(call.Receiver, output, depth + 1);
				break;
			case BoundBinaryExpression binary:
				ExplainCalls(binary.Left, output, depth);
				ExplainCalls(binary.Right, output, depth);
				break;
			case BoundConversionExpression conversion:
				ExplainCalls(conversion.Operand, output, depth);
				break;
			case BoundIndexExpression index:
				ExplainCalls(index.Target, output, depth);
				ExplainCalls(index.Index, output, depth);
				break;
		}
	}

	private static string ParameterName(BoundCallExpression call, int index) =>
		index < call.Function.GenericParameters.Count
			? call.Function.GenericParameters[index].Name
			: $"T{index}";

	private static string Describe(string path, string code, string message, Kern.Core.Diagnostics.TextSpan span)
	{
		string where = path.Length > 0 ? path + ":" : $"{span.Start}:";
		return $"{where} ошибка {code}: {message}";
	}
}
