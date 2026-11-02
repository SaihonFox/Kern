using Kern.Cli.Infrastructure;

namespace Kern.Cli.Build;

/// <summary>
/// <c>kernc dist</c> и задача <c>&lt;цель&gt;:dist</c>: собрать готовый к переносу каталог
/// <c>dist/&lt;имя&gt;/</c> — хост, <c>program.kbc</c> и внешние библиотеки в
/// <c>libs/&lt;имя&gt;/</c>. С <c>--native</c> внутри остаётся один самодостаточный
/// исполняемый файл (без установленного .NET).
/// </summary>
internal static class DistCompiler
{
	public static string Build(BuildSession session, TargetModel? target, CliOptions options, TextWriter stderr)
	{
		ArgumentNullException.ThrowIfNull(session);
		TargetModel selected = target ?? session.Workspace.DefaultAppTarget()
			?? throw new BuildException("в build.kern нет app-цели для dist");
		if (selected.IsLibrary)
			throw new BuildException($"цель '{selected.Name}' — библиотека; dist требует app-цель");

		string directory = Path.Combine(session.Root, "dist", selected.Name);
		if (options.Native)
			return NativeCompiler.BuildProjectInto(session, selected, directory, options, stderr);
		return ExeCompiler.BuildInto(session, selected, directory);
	}
}
