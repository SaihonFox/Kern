using System.Text;
using Kern.Bytecode;
using Kern.Bytecode.Serialization;

namespace Kern.Cli.Kbc;

/// <summary>Ошибка подключения .kbc-библиотеки (нет файла, битый формат, несовместимость при линковке).</summary>
public sealed class KbcException(string message) : Exception(message);

/// <summary>Библиотека .kbc, подключённая через `kbc = [...]` в kern.toml.</summary>
public sealed record KbcLib(string Path, KbcModule Module);

/// <summary>
/// Чтение `kbc = ["libs/mathx.kbc", "other.kbc"]` из kern.toml (пути — от папки проекта; допустима и одиночная строка).
/// После этого модули библиотеки доступны обычным `use mathx[vec2]`.
/// </summary>
public static class KbcConfig
{
	private static readonly Dictionary<string, (DateTime Stamp, KbcModule Module)> Cache = [];

	public static IReadOnlyList<KbcLib> Load(string? projectDir)
	{
		if (projectDir is null) return [];
		var toml = System.IO.Path.Combine(projectDir, "kern.toml");
		if (!File.Exists(toml)) return [];

		var libs = new List<KbcLib>();
		foreach (var raw in File.ReadAllLines(toml, new UTF8Encoding(false)))
		{
			var line = raw.Trim();
			if (!line.StartsWith("kbc", StringComparison.Ordinal)) continue;
			int eq = line.IndexOf('=');
			if (eq < 0 || line[..eq].Trim() != "kbc") continue;
			foreach (var item in ParseList(line[(eq + 1)..]))
			{
				var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectDir, item));
				libs.Add(new KbcLib(full, Read(full)));
			}
		}
		return libs;
	}

	private static IEnumerable<string> ParseList(string value)
	{
		value = value.Trim().TrimStart('[').TrimEnd(']');
		foreach (var part in value.Split(','))
		{
			var s = part.Trim().Trim('"');
			if (s.Length > 0) yield return s;
		}
	}

	private static KbcModule Read(string path)
	{
		if (!File.Exists(path)) throw new KbcException($"библиотека .kbc не найдена: {path}");
		var stamp = File.GetLastWriteTimeUtc(path);
		lock (Cache)
		{
			if (Cache.TryGetValue(path, out var c) && c.Stamp == stamp) return c.Module;
			try
			{
				var m = KbcReader.Read(File.ReadAllBytes(path));
				Cache[path] = (stamp, m);
				return m;
			}
			catch (Exception e) when (e is not KbcException)
			{
				throw new KbcException($"не удалось прочитать {path}: {e.Message}");
			}
		}
	}
}
