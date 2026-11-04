using System.Collections.Concurrent;
using System.Diagnostics;
using Kern.Bytecode;

namespace Kern.Vm.Execution;

/// <summary>
/// Профилировщик VM (<c>kernc run --profile</c>): число вызовов, включительное/собственное
/// время, дерево вызовов и счётчики по строкам. Включается только явно. Кадры ищутся по
/// токену вызова, а не по стеку потока: <c>await</c> может продолжить кадр на другом потоке,
/// и время всё равно вернётся своему вызывающему.
/// </summary>
public sealed class Profiler
{
	private sealed class Node(FunctionDef function, Node? parent)
	{
		public FunctionDef Function { get; } = function;
		public Node? Parent { get; } = parent;
		public Dictionary<FunctionDef, Node> Children { get; } = new(ReferenceEqualityComparer.Instance);
		public long Calls;
		public double Milliseconds;
		public double SelfMilliseconds;
	}

	private sealed class CallFrame(FunctionDef function, long started, Node node, CallFrame? caller)
	{
		public FunctionDef Function { get; } = function;
		public long Started { get; } = started;
		public Node Node { get; } = node;
		public CallFrame? Caller { get; } = caller;
		public double ChildMilliseconds { get; set; }
	}

	private readonly Dictionary<FunctionDef, Entry> _functions = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<(string File, int Line), long> _lines = [];
	private readonly Dictionary<FunctionDef, int[]> _lineMaps = new(ReferenceEqualityComparer.Instance);
	private readonly ConcurrentDictionary<long, CallFrame> _frames = new();
	private readonly List<Node> _roots = [];
	private readonly object _lock = new();
	private long _tokens;

	/// <summary>Плоская агрегация для отчёта (совместима с прежним форматом).</summary>
	private sealed class Entry
	{
		public long Calls;
		public double Milliseconds;
		public double SelfMilliseconds;
	}

	/// <summary>
	/// Начать вызов: увеличить счётчик и вернуть токен для <see cref="EndCall"/>.
	/// <paramref name="parentToken"/> — токен вызывающего кадра (0 — верхний уровень).
	/// </summary>
	public long StartCall(FunctionDef function, long parentToken = 0)
	{
		long started = Stopwatch.GetTimestamp();
		long token = Interlocked.Increment(ref _tokens);
		CallFrame? caller = parentToken != 0 && _frames.TryGetValue(parentToken, out CallFrame? found)
			? found
			: null;
		var node = new Node(function, caller?.Node);
		lock (_lock)
		{
			if (caller is null)
				_roots.Add(node);
			else if (!caller.Node.Children.TryGetValue(function, out Node? existing))
				caller.Node.Children[function] = node;
			else
				node = existing;

			node.Calls++;
			if (!_functions.TryGetValue(function, out Entry? entry))
				_functions[function] = entry = new Entry();
			entry.Calls++;
		}

		_frames[token] = new CallFrame(function, started, node, caller);
		return token;
	}

	/// <summary>
	/// Завершить вызов: добавить включительное и собственное время. Собственное время —
	/// общее минус время вложенных вызовов, поэтому рекурсия и вложенные функции не
	/// задваивают счёт. Токен ищется глобально: async-кадр может завершиться на другом потоке.
	/// </summary>
	public void EndCall(FunctionDef function, long token)
	{
		if (token == 0 || !_frames.TryRemove(token, out CallFrame? frame))
			return;

		double milliseconds = Stopwatch.GetElapsedTime(frame.Started).TotalMilliseconds;
		double self = Math.Max(0, milliseconds - frame.ChildMilliseconds);
		if (frame.Caller is not null)
			frame.Caller.ChildMilliseconds += milliseconds;

		lock (_lock)
		{
			frame.Node.Milliseconds += milliseconds;
			frame.Node.SelfMilliseconds += self;
			if (_functions.TryGetValue(function, out Entry? entry))
			{
				entry.Milliseconds += milliseconds;
				entry.SelfMilliseconds += self;
			}
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

	/// <summary>Печать отчёта: функции, дерево вызовов и горячие строки.</summary>
	public void WriteReport(TextWriter output)
	{
		ArgumentNullException.ThrowIfNull(output);
		List<KeyValuePair<FunctionDef, Entry>> functions;
		List<Node> roots;
		List<KeyValuePair<(string File, int Line), long>> lines;
		lock (_lock)
		{
			functions = [.. _functions.OrderByDescending(pair => pair.Value.Milliseconds)];
			roots = [.. _roots.OrderByDescending(node => node.Milliseconds)];
			lines = [.. _lines.OrderByDescending(pair => pair.Value)];
		}

		output.WriteLine("; профиль: функции");
		output.WriteLine("; время — включительное и собственное (мс)");
		if (functions.Count == 0)
			output.WriteLine("; вызовов не было");
		foreach (var (function, entry) in functions)
			output.WriteLine($"{function.Name,-40} {entry.Calls,10} {entry.Milliseconds,12:F3} {entry.SelfMilliseconds,12:F3}");

		output.WriteLine("; дерево вызовов");
		if (roots.Count == 0)
			output.WriteLine("; нет данных о вызовах");
		var seen = 0;
		foreach (Node root in roots)
			WriteNode(output, root, 0, ref seen, 200);

		output.WriteLine("; горячие строки");
		if (lines.Count == 0)
			output.WriteLine("; нет данных о строках (нужна отладочная информация)");
		foreach (var ((file, line), count) in lines.Take(20))
			output.WriteLine($"{file}:{line} {count}");
	}

	private static void WriteNode(TextWriter output, Node node, int depth, ref int seen, int limit)
	{
		if (seen++ >= limit)
			return;
		output.WriteLine(
			$"{new string(' ', depth * 2)}{node.Function.Name,-38} {node.Calls,8} {node.Milliseconds,12:F3} {node.SelfMilliseconds,12:F3}");
		foreach (Node child in node.Children.Values.OrderByDescending(item => item.Milliseconds))
			WriteNode(output, child, depth + 1, ref seen, limit);
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
