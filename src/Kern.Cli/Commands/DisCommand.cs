using Kern.Bytecode;

namespace Kern.Cli.Commands;

/// <summary>
/// <c>kernc dis &lt;файл.kbc&gt;</c>: читает модуль и печатает существующий
/// <see cref="Disassembler"/>. Компиляция исходников в этот срез не входит —
/// принимается только готовый .kbc.
/// </summary>
internal static class DisCommand
{
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		if (args.Length == 0)
		{
			stderr.WriteLine("ошибка: укажите файл .kbc: kernc dis <файл.kbc>");
			return 2;
		}

		if (args.Length > 1)
		{
			stderr.WriteLine($"ошибка: команда dis принимает один файл, получено аргументов: {args.Length}");
			return 2;
		}

		string path = args[0];
		if (path.StartsWith('-'))
		{
			stderr.WriteLine($"ошибка: неизвестный параметр '{path}'");
			return 2;
		}

		if (!path.EndsWith(".kbc", StringComparison.OrdinalIgnoreCase))
		{
			stderr.WriteLine($"ошибка: kernc dis принимает файл .kbc: {path}");
			return 2;
		}

		if (!File.Exists(path))
		{
			stderr.WriteLine($"ошибка: файл не найден: {path}");
			return 2;
		}

		try
		{
			KbcModule module = KbcReader.Read(File.ReadAllBytes(path));
			stdout.Write(Disassembler.Disassemble(module));
			return 0;
		}
		catch (KbcFormatException ex)
		{
			stderr.WriteLine($"ошибка: {path}: {ex.Message}");
			return 1;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			stderr.WriteLine($"ошибка: не удалось прочитать {path}: {ex.Message}");
			return 1;
		}
	}
}
