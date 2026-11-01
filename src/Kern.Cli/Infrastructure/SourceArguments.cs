namespace Kern.Cli.Infrastructure;

/// <summary>Что передали команде: один файл <c>.kern</c> или папку проекта.</summary>
internal enum SourceKind
{
	File,
	Project,
}

/// <summary>Флаги компиляции из командной строки (docs/BUILDER.md §7): <c>-O0..-O2</c>, <c>--time</c>, <c>-p</c>.</summary>
internal sealed record CliOptions
{
	public int? Optimize { get; init; }

	public bool Time { get; init; }

	public string? Profile { get; init; }

	public CompileOptions Apply(CompileOptions defaults) =>
		Optimize is { } level ? defaults with { Optimize = level } : defaults;
}

/// <summary>
/// Разбор аргументов <c>run</c>/<c>build</c>/<c>check</c>: ровно один исходник — файл
/// <c>.kern</c> или существующая папка проекта (docs/BUILDER.md §2). У <c>build</c>
/// дополнительно <c>-o &lt;файл.kbc&gt;</c>. У <c>run</c> всё после исходника — аргументы
/// программы (spec §13), у <c>build</c>/<c>check</c> любой лишний параметр — ошибка.
/// </summary>
internal static class SourceArguments
{
	public static bool TryParse(
		string[] args,
		string command,
		bool allowOutput,
		bool allowProgramArguments,
		TextWriter stderr,
		out string source,
		out SourceKind kind,
		out string? output,
		out IReadOnlyList<string> programArguments,
		out CliOptions options)
	{
		source = string.Empty;
		kind = SourceKind.File;
		output = null;
		programArguments = [];
		options = new CliOptions();
		string? first = null;
		List<string> rest = [];
		List<string> program = [];

		for (var index = 0; index < args.Length; index++)
		{
			string argument = args[index];
			if (argument == "-o" && allowOutput)
			{
				if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
				{
					stderr.WriteLine("ошибка: параметр -o требует путь к файлу .kbc");
					return false;
				}

				if (output is not null)
				{
					stderr.WriteLine("ошибка: параметр -o указан дважды");
					return false;
				}

				output = args[++index];
				continue;
			}

			if (argument is "-O0" or "-O1" or "-O2")
			{
				options = options with { Optimize = argument[2] - '0' };
				continue;
			}

			if (argument == "--time")
			{
				options = options with { Time = true };
				continue;
			}

			if (argument == "-p")
			{
				if (index + 1 >= args.Length)
				{
					stderr.WriteLine("ошибка: параметр -p требует имя профиля");
					return false;
				}

				options = options with { Profile = args[++index] };
				continue;
			}

			// Всё после исходника — аргументы программы, включая строки с '-'.
			if (first is not null && allowProgramArguments)
			{
				program.Add(argument);
				continue;
			}

			if (argument.StartsWith('-'))
			{
				stderr.WriteLine($"ошибка: неизвестный параметр '{argument}'");
				return false;
			}

			if (first is null)
				first = argument;
			else
				rest.Add(argument);
		}

		if (first is null)
		{
			stderr.WriteLine($"ошибка: укажите файл .kern или папку проекта: {Usage(command, allowOutput, allowProgramArguments)}");
			return false;
		}

		if (rest.Count > 0)
		{
			stderr.WriteLine(
				$"ошибка: команда {command} принимает один файл .kern или папку проекта, получено аргументов: {rest.Count + 1}");
			return false;
		}

		source = first;
		if (Directory.Exists(source))
		{
			kind = SourceKind.Project;
		}
		else
		{
			kind = SourceKind.File;
			if (!source.EndsWith(".kern", StringComparison.OrdinalIgnoreCase))
			{
				stderr.WriteLine($"ошибка: kernc {command} принимает файл .kern или папку проекта: {source}");
				return false;
			}

			if (!File.Exists(source))
			{
				stderr.WriteLine($"ошибка: файл не найден: {source}");
				return false;
			}
		}

		if (output is not null && !output.EndsWith(".kbc", StringComparison.OrdinalIgnoreCase))
		{
			stderr.WriteLine($"ошибка: параметр -o принимает файл .kbc: {output}");
			return false;
		}

		programArguments = program;
		return true;
	}

	private static string Usage(string command, bool allowOutput, bool allowProgramArguments)
	{
		string file = allowOutput
			? $"kernc {command} <файл.kern> [-o <файл.kbc>]"
			: allowProgramArguments
				? $"kernc {command} <файл.kern> [аргумент ...]"
				: $"kernc {command} <файл.kern>";
		string project = allowOutput
			? $"kernc {command} <папка> [-o <файл.kbc>]"
			: allowProgramArguments
				? $"kernc {command} <папка> [аргумент ...]"
				: $"kernc {command} <папка>";
		return $"{file} | {project}";
	}
}
