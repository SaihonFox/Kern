using Kern.Cli.Build;
using Kern.Cli.Infrastructure;

namespace Kern.Cli.Commands;

/// <summary><c>kernc dist &lt;папка&gt;</c>: готовый к переносу <c>dist/&lt;имя&gt;/</c>.</summary>
internal static class DistCommand
{
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		if (!SourceArguments.TryParse(
			args, "dist", allowOutput: false, allowProgramArguments: false, stderr,
			out string path, out SourceKind kind, out _, out _, out CliOptions options))
			return 2;
		if (kind != SourceKind.Project)
		{
			stderr.WriteLine("ошибка: dist принимает папку проекта: kernc dist <папка>");
			return 2;
		}

		try
		{
			BuildSession session = BuildSession.Open(path);
			session.OptimizeOverride = options.Optimize;
			session.ProfileName = options.Profile;
			session.WarningsAsErrorsOverride = options.DenyWarnings ? true : null;
			stdout.WriteLine(DistCompiler.Build(session, target: null, options, stderr));
			return 0;
		}
		catch (BuildException exception)
		{
			stderr.WriteLine($"ошибка: {exception.Message}");
			return 1;
		}
	}
}
