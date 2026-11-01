using Kern.Cli;

namespace Kern.Cli.Build;

/// <summary>
/// Выполняет <c>prebuild.txt</c> проекта (по одной команде <c>kernc</c> в строке)
/// перед check/run; нужен для примеров с <c>t.kbc</c>, например 31_kbc.
/// </summary>
internal static class PrebuildRunner
{
	public static int Run(string root, TextWriter stderr)
	{
		string path = Path.Combine(root, "prebuild.txt");
		if (!File.Exists(path))
			return 0;

		string? previous = null;
		try
		{
			previous = Environment.CurrentDirectory;
			Directory.SetCurrentDirectory(root);
			foreach (string raw in File.ReadLines(path))
			{
				string line = raw.Trim();
				if (line.Length == 0 || line.StartsWith('#'))
					continue;
				string[] args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				int code = KernCli.Run(args, TextWriter.Null, stderr);
				if (code != 0)
					return code;
			}

			return 0;
		}
		finally
		{
			if (previous is not null)
				Directory.SetCurrentDirectory(previous);
		}
	}
}
