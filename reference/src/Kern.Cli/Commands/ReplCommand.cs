using System.Text;
using Kern.Bytecode;
using Kern.Cli.Infrastructure;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Lexing;
using Kern.Compiler.Semantics;
using Kern.Core;

namespace Kern.Cli.Commands;

/// <summary>
/// `kernc repl`: интерактивная сессия. Объявления (`fun`, `type`, `unit`, `use`) накапливаются; строки-выражения вычисляются и печатаются;
/// объявления переменных и присваивания запоминаются и повторно выполняются перед каждой следующей строкой
/// (поэтому инициализаторы переменных должны быть без побочных эффектов).
/// </summary>
internal static class ReplCommand
{
	private const string Help = """
		Команды REPL:
		  :help      эта справка
		  :decls     показать накопленные объявления
		  :history   показать запомненные операторы
		  :reset     очистить сессию
		  :quit      выход (или Ctrl+D)
		Многострочный ввод продолжается, пока скобки не закрыты.
		""";

	public static int Run(int opt)
	{
		Console.Out.WriteLine($"Kern {Program.Version} REPL. :help — справка, :quit — выход.");
		var uses = new List<string>();
		var decls = new Dictionary<string, string>();
		var history = new List<string>();

		while (true)
		{
			string? input = ReadEntry();
			if (input is null) return 0;
			string trimmed = input.Trim();
			if (trimmed.Length == 0) continue;

			if (trimmed.StartsWith(':'))
			{
				switch (trimmed)
				{
					case ":quit" or ":q" or ":exit": return 0;
					case ":help": Console.Out.WriteLine(Help); break;
					case ":reset": uses.Clear(); decls.Clear(); history.Clear(); Console.Out.WriteLine("сессия очищена"); break;
					case ":decls": foreach (var d in uses.Concat(decls.Values)) Console.Out.WriteLine(d); break;
					case ":history": foreach (var h in history) Console.Out.WriteLine(h); break;
					default: Console.Out.WriteLine($"неизвестная команда '{trimmed}', см. :help"); break;
				}
				continue;
			}

			var kind = Classify(trimmed, out string key);
			if (kind == EntryKind.Use)
			{
				var next = uses.Append(trimmed).ToList();
				if (TryCompile(Program_(next, decls.Values, history, null), opt, out _, out var err)) uses = next;
				else Console.Error.WriteLine(err);
			}
			else if (kind == EntryKind.Decl)
			{
				var next = new Dictionary<string, string>(decls) { [key] = trimmed };
				if (TryCompile(Program_(uses, next.Values, history, null), opt, out _, out var err)) decls = next;
				else Console.Error.WriteLine(err);
			}
			else
			{
				// сначала как выражение (печатаем значение), затем как оператор (запоминаем)
				if (TryCompile(Program_(uses, decls.Values, history, $"println({trimmed})"), opt, out var module, out _))
				{
					Execute(module!);
					continue;
				}
				if (TryCompile(Program_(uses, decls.Values, history, trimmed), opt, out module, out var err))
				{
					Execute(module!);
					if (IsRemembered(trimmed)) history.Add(trimmed);
				}
				else Console.Error.WriteLine(err);
			}
		}
	}

	private static string Program_(IEnumerable<string> uses, IEnumerable<string> decls, IEnumerable<string> history, string? last)
	{
		var sb = new StringBuilder();
		foreach (var u in uses) sb.AppendLine(u);
		foreach (var d in decls) sb.AppendLine(d);
		sb.AppendLine("async fun main() {");
		foreach (var h in history) sb.AppendLine(h);
		if (last is not null) sb.AppendLine(last);
		sb.AppendLine("}");
		return sb.ToString();
	}

	private static void Execute(KbcModule module)
	{
		try { Pipeline.CreateVm(module).RunMain([]); }
		catch (Exception e) { Console.Error.WriteLine("error: " + e.Message); }
	}

	private static bool TryCompile(string source, int opt, out KbcModule? module, out string error)
	{
		module = null;
		error = "";
		try
		{
			var project = Pipeline.CreateLoader().LoadText("repl", "repl.kern", source);
			if (Errors(project.Diagnostics, out error)) return false;
			int seen = project.Diagnostics.Count;
			var program = SemanticAnalyzer.Analyze(project);
			if (Errors(project.Diagnostics.Skip(seen), out error)) return false;
			module = CodeGenerator.Generate(program, "repl", opt);
			return true;
		}
		catch (KernCompileException e) { error = e.Message; return false; }
	}

	private static bool Errors(IEnumerable<Diagnostic> diags, out string text)
	{
		var errs = diags.Where(d => d.Severity == Severity.Error).Select(DescribeError).ToList();
		text = string.Join("\n", errs);
		return errs.Count > 0;
	}

	/// <summary>Сообщение без номера строки программы-обёртки (он пользователю ни к чему).</summary>
	private static string DescribeError(Diagnostic d) => $"error {d.Code}: {d.Message}";

	private enum EntryKind { Use, Decl, Statement }

	private static EntryKind Classify(string text, out string key)
	{
		key = text;
		var tokens = Lexer.Lex(new SourceFile("repl", text), new DiagnosticBag());
		int i = 0;
		while (i < tokens.Count && tokens[i].Kind is TokenKind.KwPub or TokenKind.KwPriv or TokenKind.KwOpen or TokenKind.KwAsync) i++;
		if (i >= tokens.Count) return EntryKind.Statement;
		var k = tokens[i].Kind;
		if (k == TokenKind.KwUse) return EntryKind.Use;
		if (k is TokenKind.KwType or TokenKind.KwUnit or TokenKind.KwEnum
			|| (k == TokenKind.KwFun && i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Ident)
			|| k is TokenKind.Hash or TokenKind.HashBracket or TokenKind.KwAttribute)
		{
			var name = tokens.Skip(i + 1).FirstOrDefault(t => t.Kind == TokenKind.Ident)?.Text ?? text;
			key = $"{k}:{name}";
			return EntryKind.Decl;
		}
		return EntryKind.Statement;
	}

	/// <summary>Запоминаем объявления переменных и присваивания; вызовы и выражения повторять незачем.</summary>
	private static bool IsRemembered(string text)
	{
		var tokens = Lexer.Lex(new SourceFile("repl", text), new DiagnosticBag());
		return tokens.Any(t => t.Kind is TokenKind.Eq or TokenKind.PlusEq or TokenKind.MinusEq or TokenKind.StarEq or TokenKind.SlashEq or TokenKind.PercentEq);
	}

	private static string? ReadEntry()
	{
		var sb = new StringBuilder();
		Console.Out.Write("kern> ");
		while (true)
		{
			string? line = Console.In.ReadLine();
			if (line is null) return sb.Length == 0 ? null : sb.ToString();
			sb.AppendLine(line);
			if (sb.ToString().TrimStart().StartsWith(':') || Balanced(sb.ToString())) return sb.ToString();
			Console.Out.Write("  ... ");
		}
	}

	private static bool Balanced(string text)
	{
		var tokens = Lexer.Lex(new SourceFile("repl", text), new DiagnosticBag());
		int depth = 0;
		foreach (var t in tokens)
		{
			if (t.Kind is TokenKind.LParen or TokenKind.LBracket or TokenKind.LBrace or TokenKind.DollarBracket or TokenKind.HashBracket) depth++;
			else if (t.Kind is TokenKind.RParen or TokenKind.RBracket or TokenKind.RBrace) depth--;
		}
		return depth <= 0;
	}
}
