using Kern.Cli.Build;
using Kern.Cli.Infrastructure;

namespace Kern.Cli.Commands;

/// <summary><c>kernc dll &lt;папка&gt; [-o &lt;файл.kbc&gt;]</c>: библиотека <c>.kbc</c>
/// с интерфейсными исходниками (spec round 25).</summary>
internal static class DllCommand
{
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		if (!SourceArguments.TryParse(
			args, "dll", allowOutput: true, allowProgramArguments: false, stderr,
			out string path, out SourceKind kind, out string? output, out _, out CliOptions options))
			return 2;
		if (kind != SourceKind.Project)
		{
			stderr.WriteLine("ошибка: dll принимает папку проекта: kernc dll <папка>");
			return 2;
		}

		try
		{
			BuildSession session = BuildSession.Open(path);
			session.OptimizeOverride = options.Optimize;
			session.ProfileName = options.Profile;
			stdout.WriteLine(DllCompiler.Build(session, target: null, output));
			return 0;
		}
		catch (BuildException exception)
		{
			stderr.WriteLine($"ошибка: {exception.Message}");
			return 1;
		}
	}
}
