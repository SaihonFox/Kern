using System.Text;
using Kern.Lsp;
using Kern.Lsp.Protocol;

namespace Kern.Cli.Commands;

/// <summary>
/// <c>kernc lsp</c>: запустить языковой сервер на stdio. Используется редакторами
/// (VS Code, Neovim и др.); протокол — JSON-RPC 2.0 с <c>Content-Length</c>.
/// </summary>
internal static class LspCommand
{
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		_ = stdout;
		for (var index = 0; index < args.Length; index++)
		{
			string argument = args[index];
			// vscode-languageclient добавляет эти флаги к любой stdio-команде.
			if (argument is "--stdio" or "--pipe")
				continue;
			if (argument.StartsWith("--clientProcessId=", StringComparison.Ordinal))
				continue;
			if (argument == "--clientProcessId" && index + 1 < args.Length)
			{
				index++;
				continue;
			}

			stderr.WriteLine($"ошибка: kernc lsp: неизвестный аргумент '{argument}' (сервер общается по stdio)");
			return 2;
		}

		Console.InputEncoding = Encoding.UTF8;
		Console.OutputEncoding = new UTF8Encoding(false);
		using Stream input = Console.OpenStandardInput();
		using Stream output = Console.OpenStandardOutput();
		var server = new LspServer(json => JsonRpc.WriteMessage(output, json));
		while (!server.ExitRequested)
		{
			string? message = JsonRpc.ReadMessage(input);
			if (message is null)
				break;
			string? response = server.Handle(message);
			if (response is not null)
				JsonRpc.WriteMessage(output, response);
		}

		return 0;
	}
}
