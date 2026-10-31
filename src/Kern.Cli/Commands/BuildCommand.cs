using Kern.Bytecode;
using Kern.Cli.Infrastructure;

namespace Kern.Cli.Commands;

/// <summary>
/// <c>kernc build &lt;файл.kern | папка&gt; [-o &lt;файл.kbc&gt;]</c>: компилирует один файл
/// или проект по соглашению и пишет байткод. Для файла без <c>-o</c> путь тот же, что у
/// исходника, с расширением <c>.kbc</c>; для папки — <c>&lt;папка&gt;/out/&lt;имя&gt;.kbc</c>.
/// Точка входа не требуется: модуль без <c>main</c> — обычная библиотека.
/// </summary>
internal static class BuildCommand
{
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		if (!SourceArguments.TryParse(
			args, "build", allowOutput: true, allowProgramArguments: false, stderr,
			out string path, out SourceKind kind, out string? output, out _))
			return 2;

		CompileResult result;
		string target;
		if (kind == SourceKind.Project)
		{
			if (!ProjectSources.TryLoad(path, out ProjectSources? project, out string? error))
			{
				stderr.WriteLine($"ошибка: {error}");
				return 1;
			}

			result = SourceCompiler.Compile(project!);
			target = project!.OutputKbcPath(output);
		}
		else
		{
			result = SourceCompiler.Compile(path);
			target = output ?? Path.ChangeExtension(path, ".kbc");
		}

		SourceCompiler.Report(result, stderr);
		if (!result.Success)
			return 1;

		try
		{
			if (kind == SourceKind.Project && Path.GetDirectoryName(target) is { Length: > 0 } directory)
				Directory.CreateDirectory(directory);
			File.WriteAllBytes(target, KbcWriter.Write(result.Program!.Module));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			stderr.WriteLine($"ошибка: не удалось записать {target}: {ex.Message}");
			return 1;
		}

		stdout.WriteLine(target);
		return 0;
	}
}
