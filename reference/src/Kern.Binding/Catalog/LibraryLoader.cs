using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

namespace Kern.Binding.Catalog;

/// <summary>
/// Подключение внешних C#-библиотек Kern: сборки `Kern.Lib.*.dll` с атрибутами [KernType]/[KernModule]. Ищутся в корнях:
/// переменная <c>KERN_LIBS</c> (список путей), папка <c>libs</c> рядом с kernc, <c>libs/_out</c> выше по дереву (репозиторий),
/// <c>~/.kern/libs</c>, <c>libs</c> проекта и пути из <c>libs = [...]</c> в kern.toml. Корень — папка (DLL лежат в ней или в её
/// подпапках первого уровня) либо сам .dll. Зависимости библиотеки (Npgsql…) ищутся рядом с её DLL.
/// Загрузка в контекст по умолчанию: тем же способом NativeKey.Find потом находит члены при исполнении.
/// </summary>
public static class LibraryLoader
{
	private static readonly object Gate = new();
	private static readonly Dictionary<string, Assembly> Loaded = new(StringComparer.OrdinalIgnoreCase);
	private static readonly List<string> ProbeDirs = [];
	private static bool _hooked;

	/// <summary>Стандартные корни поиска для проекта (папка проекта может быть null).</summary>
	public static IReadOnlyList<string> StandardRoots(string? projectDir)
	{
		var roots = new List<string>();
		if (Environment.GetEnvironmentVariable("KERN_LIBS") is { Length: > 0 } env)
			roots.AddRange(env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
		string baseDir = AppContext.BaseDirectory;
		roots.Add(Path.Combine(baseDir, "libs"));
		for (var d = new DirectoryInfo(baseDir); d?.Parent is not null; d = d.Parent)
		{
			string dev = Path.Combine(d.FullName, "libs", "_out");
			if (Directory.Exists(dev)) { roots.Add(dev); break; }
		}
		roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kern", "libs"));
		if (projectDir is not null)
		{
			roots.Add(Path.Combine(projectDir, "libs"));
			roots.AddRange(ReadTomlLibs(projectDir));
		}
		return roots;
	}

	/// <summary>`libs = ["путь", ...]` из kern.toml проекта (относительные пути — от папки проекта).</summary>
	public static IEnumerable<string> ReadTomlLibs(string projectDir)
	{
		string toml = Path.Combine(projectDir, "kern.toml");
		if (!File.Exists(toml)) yield break;
		var m = Regex.Match(File.ReadAllText(toml), @"^\s*libs\s*=\s*\[(?<items>[^\]]*)\]", RegexOptions.Multiline);
		if (!m.Success) yield break;
		foreach (Match item in Regex.Matches(m.Groups["items"].Value, "\"([^\"]+)\""))
			yield return Path.GetFullPath(item.Groups[1].Value, projectDir);
	}

	/// <summary>Загружает найденные библиотеки (повторные вызовы безопасны) и возвращает все загруженные сборки.</summary>
	public static IReadOnlyList<Assembly> Load(IEnumerable<string> roots)
	{
		lock (Gate)
		{
			EnsureHook();
			foreach (var root in roots)
			{
				if (File.Exists(root)) TryLoad(root);
				else if (Directory.Exists(root))
				{
					foreach (var f in Directory.EnumerateFiles(root, "Kern.Lib.*.dll")) TryLoad(f);
					foreach (var sub in Directory.EnumerateDirectories(root))
						foreach (var f in Directory.EnumerateFiles(sub, "Kern.Lib.*.dll")) TryLoad(f);
				}
			}
			return [.. Loaded.Values];
		}
	}

	private static void TryLoad(string path)
	{
		string name = Path.GetFileNameWithoutExtension(path);
		if (Loaded.ContainsKey(name)) return;
		string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
		if (!ProbeDirs.Contains(dir)) ProbeDirs.Add(dir);
		try { Loaded[name] = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path)); }
		catch (Exception e) when (e is BadImageFormatException or FileLoadException or IOException)
		{
			Console.Error.WriteLine($"warning: библиотека {path} не загружена: {e.Message}");
		}
	}

	private static void EnsureHook()
	{
		if (_hooked) return;
		_hooked = true;
		// нативные библиотеки зависимостей (e_sqlite3.dll у SQLite): лежат в <папка библиотеки>/runtimes/<rid>/native или рядом с DLL.
		// Библиотека загружена из произвольной папки, поэтому стандартный поиск .NET их не находит.
		AssemblyLoadContext.Default.ResolvingUnmanagedDll += (assembly, name) =>
		{
			lock (Gate)
			{
				string rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
				string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
				string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
				string[] names = [name, name + ".dll", "lib" + name + ".so", "lib" + name + ".dylib"];
				string[] rids = [rid, $"{os}-{arch}", os];
				foreach (var dir in ProbeDirs)
					foreach (var r in rids)
						foreach (var n in names)
						{
							string candidate = Path.Combine(dir, "runtimes", r, "native", n);
							if (File.Exists(candidate) && System.Runtime.InteropServices.NativeLibrary.TryLoad(candidate, out var handle)) return handle;
						}
				foreach (var dir in ProbeDirs)
					foreach (var n in names)
					{
						string candidate = Path.Combine(dir, n);
						if (File.Exists(candidate) && System.Runtime.InteropServices.NativeLibrary.TryLoad(candidate, out var handle)) return handle;
					}
			}
			return IntPtr.Zero;
		};
		AssemblyLoadContext.Default.Resolving += (ctx, an) =>
		{
			lock (Gate)
				foreach (var dir in ProbeDirs)
				{
					string candidate = Path.Combine(dir, an.Name + ".dll");
					if (File.Exists(candidate)) return ctx.LoadFromAssemblyPath(candidate);
				}
			return null;
		};
	}
}
