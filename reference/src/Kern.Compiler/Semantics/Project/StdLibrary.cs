using System.Reflection;
using System.Text;

namespace Kern.Compiler.Semantics.Project;

/// <summary>Источник исходников стандартной библиотеки (`std/**/*.kern`).</summary>
public interface IStdSource
{
	/// <summary>Пары (логический путь `std/kern/text.kern`, текст).</summary>
	IEnumerable<(string Path, string Text)> Files();
}

/// <summary>Читает встроенные ресурсы сборки Kern.Std (LogicalName = `std/<подпуть>.kern`).</summary>
public sealed class EmbeddedStdSource(Assembly assembly) : IStdSource
{
	public IEnumerable<(string Path, string Text)> Files()
	{
		foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".kern", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
		{
			using var s = assembly.GetManifestResourceStream(name)!;
			using var r = new StreamReader(s, new UTF8Encoding(false), true);
			yield return (name.Replace('\\', '/'), r.ReadToEnd());
		}
	}
}

/// <summary>Std из папки на диске (разработка/тесты).</summary>
public sealed class DirectoryStdSource(string root) : IStdSource
{
	public IEnumerable<(string Path, string Text)> Files()
	{
		foreach (var f in Directory.EnumerateFiles(root, "*.kern", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
			yield return ("std/" + Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f, new UTF8Encoding(false)));
	}
}

/// <summary>Пустой std (тесты компилятора без библиотеки).</summary>
public sealed class EmptyStdSource : IStdSource
{
	public IEnumerable<(string Path, string Text)> Files() => [];
}

public static class StdLibrary
{
	/// <summary>`std/kern/text.kern` → модуль `kern.text`; `std/web/server.kern` → `web.server`.</summary>
	public static string ModuleNameOf(string logicalPath)
	{
		var p = logicalPath.Replace('\\', '/');
		if (p.StartsWith("std/", StringComparison.Ordinal)) p = p[4..];
		if (p.EndsWith(".kern", StringComparison.Ordinal)) p = p[..^5];
		return p.Replace('/', '.');
	}
}
