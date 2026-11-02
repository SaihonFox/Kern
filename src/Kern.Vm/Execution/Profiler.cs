using System.Diagnostics;
using Kern.Bytecode;

namespace Kern.Vm.Execution;

/// <summary>
/// Профилировщик VM (<c>kern run --profile</c>): число вызовов и включительное время по
/// функциям плюс счётчики выполнения по строкам исходника. Включается только явно, поэтому
/// обычное исполнение не платит за замеры; строки берутся из таблиц <c>.kbc</c>.
/// </summary>
public sealed class Profiler
{
	private sealed class Entry
	{
		public long Calls;
		public double Milliseconds;
	}

	private readonly Dictionary<FunctionDef, Entry> _functions = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<(string File, int Line), long> _lines = [];
	private readonly Dictionary<FunctionDef, int[]> _lineMaps = new(ReferenceEqualityComparer.Instance);
	private readonly object _lock = new();

	/// <summary>Начать вызов: увеличить счётчик и вернуть метку времени для <see cref="EndCall"/>.</summary>
	public long StartCall(FunctionDef function)
	{
		lock (_lock)
		{
			if (!_functions.TryGetValue(function, out Entry? entry))
				_functions[function] = entry = new Entry();
			entry.Calls++;
		}
		return Stopwatch.GetTimestamp();
	}

	/// <summary>Завершить вызов: добавить включительное время.</summary>
	public void EndCall(FunctionDef function, long started)
	{
		if (started == 0)
			return;
		double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
		lock (_lock)
		{
			if (_functions.TryGetValue(function, out Entry? entry))
				entry.Milliseconds += milliseconds;
		}
	}

	/// <summary>Учесть выполнение инструкции по адресу <paramref name="pc"/>.</summary>
	public void Sample(FunctionDef function, int pc)
	{
		if (!_lineMaps.TryGetValue(function, out int[]? map))
		{
			map = BuildLineMap(function);
			_lineMaps[function] = map;
			if (map.Length == 0)
				return;
		}
		if (pc < 0 || pc >= map.Length || map[pc] <= 0)
			return;
		string file = function.SourceFile ?? "<unknown>";
		var key = (file, map[pc]);
		lock (_lock)
			_lines[key] = _lines.TryGetValue(key, out long count) ? count + 1 : 1;
	}

	/// <summary>Печать отчёта: функции по убыванию времени, затем горячие строки.</summary>
	public void WriteReport(TextWriter output)
	{
		ArgumentNullException.ThrowIfNull(output);
		List<KeyValuePair<FunctionDef, Entry>> functions;
		List<KeyValuePair<(string File, int Line), long>> lines;
		lock (_lock)
		{
			functions = [.. _functions.OrderByDescending(pair => pair.Value.Milliseconds)];
			lines = [.. _lines.OrderByDescending(pair => pair.Value)];
		}

		output.WriteLine("; профиль: функции");
		output.WriteLine("; время — включительное (мс)");
		if (functions.Count == 0)
			output.WriteLine("; вызовов не было");
		foreach (var (function, entry) in functions)
			output.WriteLine($"{function.Name,-40} {entry.Calls,10} {entry.Milliseconds,12:F3}");

		output.WriteLine("; горячие строки");
		if (lines.Count == 0)
			output.WriteLine("; нет данных о строках (нужна отладочная информация)");
		foreach (var ((file, line), count) in lines.Take(20))
			output.WriteLine($"{file}:{line} {count}");
	}

	private static int[] BuildLineMap(FunctionDef function)
	{
		int[] map = new int[function.Code.Length];
		if (function.LineTable.Count == 0)
			return map;
		LineEntry[] entries = [.. function.LineTable.OrderBy(entry => entry.Offset)];
		for (int index = 0; index < entries.Length; index++)
		{
			int start = Math.Max(0, entries[index].Offset);
			int end = index + 1 < entries.Length ? entries[index + 1].Offset : map.Length;
			for (int position = start; position < Math.Min(end, map.Length); position++)
				map[position] = entries[index].Line;
		}
		return map;
	}
}
