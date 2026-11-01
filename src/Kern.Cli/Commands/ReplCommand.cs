namespace Kern.Cli.Commands;

/// <summary>
/// <c>kernc repl</c> (spec §16, раунд 6): построчный ввод, <c>:quit</c> — выход.
/// Каждая строка компилируется заново, объявления и присваивания повторяются.
/// </summary>
internal static class ReplCommand
{
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		if (args.Length > 0)
		{
			stderr.WriteLine("ошибка: kernc repl не принимает аргументов");
			return 2;
		}

		var session = new Repl.ReplSession();
		stdout.WriteLine("Kern REPL: вводите выражения, :quit — выход");
		while (true)
		{
			stdout.Write("kern> ");
			stdout.Flush();
			string? line = Console.ReadLine();
			if (line is null)
				return 0;
			if (line.Trim() is ":quit" or ":q" or ":exit")
				return 0;
			if (line.Trim() is ":help")
			{
				stdout.WriteLine("объявления и выражения выполняются построчно; :quit — выход");
				continue;
			}

			_ = session.Evaluate(line, stdout, stderr);
		}
	}
}
