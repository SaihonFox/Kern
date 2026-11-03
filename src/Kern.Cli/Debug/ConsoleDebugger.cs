using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Cli.Debug;

/// <summary>
/// Интерактивный отладчик CLI (docs/DEBUGGER.md): точки останова по файлу/строке/функции,
/// <c>step</c>/<c>next</c>/<c>finish</c>, просмотр локальных слотов и стека кадров.
/// Выражения не вычисляются: у байткода нет имён локальных слотов, только индексы.
/// </summary>
internal sealed class ConsoleDebugger : IDebugHook
{
	private enum Mode
	{
		None,
		Step,
		Next,
		Finish,
	}

	private sealed record Breakpoint(int Id, string? File, int? Line, string? Function)
	{
		public bool Matches(FunctionDef function, int line)
		{
			if (Function is not null)
				return string.Equals(Function, function.Name, StringComparison.Ordinal);
			if (Line is null || Line != line)
				return false;
			if (File is null)
				return true;
			return string.Equals(Path.GetFileName(File), Path.GetFileName(function.SourceFile), StringComparison.OrdinalIgnoreCase) ||
				string.Equals(File, function.SourceFile, StringComparison.OrdinalIgnoreCase);
		}

		public string Describe() => Function is not null
			? $"функция {Function}"
			: $"{File ?? "текущий файл"}:{Line}";
	}

	private readonly TextReader _input;
	private readonly TextWriter _output;
	private readonly List<Breakpoint> _breakpoints = [];
	private Mode _mode = Mode.None;
	private int _depth;
	private bool _entry = true;
	private (string Function, int Offset)? _resumed;

	public ConsoleDebugger(TextReader input, TextWriter output)
	{
		_input = input;
		_output = output;
	}

	public bool ShouldPause(FunctionDef function, int offset, int? line, int frameCount)
	{
		if (_entry)
			return string.Equals(function.Name, "main", StringComparison.Ordinal);
		if (_mode == Mode.Step)
			return true;
		if (_mode == Mode.Next && frameCount <= _depth)
			return true;
		if (_mode == Mode.Finish && frameCount < _depth)
			return true;
		if (_resumed is { } resumed &&
			string.Equals(resumed.Function, function.Name, StringComparison.Ordinal) &&
			resumed.Offset == offset)
			return false;
		foreach (Breakpoint breakpoint in _breakpoints)
			if (breakpoint.Matches(function, line ?? 0))
				return true;
		return false;
	}

	public bool Pause(DebugPauseInfo pause)
	{
		_entry = false;
		_mode = Mode.None;
		_resumed = null;
		_output.WriteLine($"остановлено: {Location(pause)}");
		while (true)
		{
			_output.Write("(dbg) ");
			_output.Flush();
			string? text = _input.ReadLine();
			if (text is null)
			{
				_output.WriteLine();
				return false;
			}

			text = text.Trim();
			if (text.Length == 0)
				text = "continue";
			int space = text.IndexOf(' ');
			string command = (space < 0 ? text : text[..space]).ToLowerInvariant();
			string argument = space < 0 ? string.Empty : text[(space + 1)..].Trim();
			switch (command)
			{
				case "c" or "cont" or "continue":
					_mode = Mode.None;
					_resumed = (pause.Function, pause.Offset);
					return true;
				case "s" or "step":
					_mode = Mode.Step;
					return true;
				case "n" or "next":
					_mode = Mode.Next;
					_depth = pause.FrameCount;
					return true;
				case "f" or "finish":
					_mode = Mode.Finish;
					_depth = pause.FrameCount;
					return true;
				case "b" or "break":
					AddBreakpoint(argument, pause);
					break;
				case "d" or "delete":
					DeleteBreakpoint(argument);
					break;
				case "i" or "info":
					PrintBreakpoints();
					break;
				case "l" or "locals":
					PrintLocals(pause);
					break;
				case "bt" or "where" or "stack":
					PrintStack(pause);
					break;
				case "p" or "print":
					PrintSlot(argument, pause);
					break;
				case "h" or "help" or "?":
					PrintHelp();
					break;
				case "q" or "quit" or "exit":
					return false;
				default:
					_output.WriteLine($"неизвестная команда '{command}'; help — список");
					break;
			}
		}
	}

	private void AddBreakpoint(string argument, DebugPauseInfo pause)
	{
		if (argument.Length == 0)
		{
			_output.WriteLine("использование: break <строка> | break <файл>:<строка> | break <функция>");
			return;
		}

		int colon = argument.LastIndexOf(':');
		if (colon > 0 && int.TryParse(argument[(colon + 1)..], out int fileLine) && fileLine > 0)
		{
			Add(new Breakpoint(NextId(), argument[..colon], fileLine, null));
			return;
		}
		if (int.TryParse(argument, out int line) && line > 0)
		{
			Add(new Breakpoint(NextId(), pause.SourceFile, line, null));
			return;
		}
		Add(new Breakpoint(NextId(), null, null, argument));
	}

	private void Add(Breakpoint breakpoint)
	{
		_breakpoints.Add(breakpoint);
		_output.WriteLine($"точка останова {breakpoint.Id}: {breakpoint.Describe()}");
	}

	private void DeleteBreakpoint(string argument)
	{
		if (argument.Equals("all", StringComparison.OrdinalIgnoreCase) || argument == "*")
		{
			_breakpoints.Clear();
			_output.WriteLine("все точки останова удалены");
			return;
		}
		if (!int.TryParse(argument, out int id) ||
			_breakpoints.RemoveAll(breakpoint => breakpoint.Id == id) == 0)
		{
			_output.WriteLine($"нет точки останова '{argument}'");
			return;
		}
		_output.WriteLine($"точка останова {id} удалена");
	}

	private void PrintBreakpoints()
	{
		if (_breakpoints.Count == 0)
		{
			_output.WriteLine("точек останова нет");
			return;
		}
		foreach (Breakpoint breakpoint in _breakpoints)
			_output.WriteLine($"{breakpoint.Id}: {breakpoint.Describe()}");
	}

	private void PrintLocals(DebugPauseInfo pause)
	{
		if (pause.Locals.Count == 0)
		{
			_output.WriteLine("(нет локальных слотов)");
			return;
		}
		for (var slot = 0; slot < pause.Locals.Count; slot++)
		{
			string name = slot < pause.ParamCount ? $"param{slot}" : $"slot{slot}";
			_output.WriteLine($"{name} = {Format(pause.Locals[slot])}");
		}
	}

	private void PrintStack(DebugPauseInfo pause)
	{
		for (var index = 0; index < pause.Stack.Count; index++)
		{
			DebugFrameInfo frame = pause.Stack[index];
			_output.WriteLine($"#{index} {Location(frame)}");
		}
	}

	private void PrintSlot(string argument, DebugPauseInfo pause)
	{
		if (!int.TryParse(argument, out int slot) || slot < 0 || slot >= pause.Locals.Count)
		{
			_output.WriteLine($"укажите слот 0..{pause.Locals.Count - 1}");
			return;
		}
		_output.WriteLine($"[{slot}] = {Format(pause.Locals[slot])}");
	}

	private void PrintHelp()
	{
		_output.WriteLine("команды:");
		_output.WriteLine("  continue|c        продолжить до следующей точки останова");
		_output.WriteLine("  step|s            войти в следующую инструкцию");
		_output.WriteLine("  next|n            следующая инструкция в текущем кадре");
		_output.WriteLine("  finish|f          до возврата из текущего кадра");
		_output.WriteLine("  break|b <…>       точка останова: строка, файл:строка или функция");
		_output.WriteLine("  delete|d <id|all> удалить точку останова");
		_output.WriteLine("  info|i            список точек останова");
		_output.WriteLine("  locals|l          локальные слоты текущего кадра");
		_output.WriteLine("  stack|bt          стек кадров");
		_output.WriteLine("  print|p <слот>    значение слота");
		_output.WriteLine("  quit|q            прервать программу");
	}

	private int NextId() => _breakpoints.Count == 0 ? 1 : _breakpoints.Max(breakpoint => breakpoint.Id) + 1;

	private static string Location(DebugPauseInfo pause) =>
		$"{ShortFile(pause.SourceFile)}:{pause.Line} в {pause.Function}";

	private static string Location(DebugFrameInfo frame) =>
		$"{ShortFile(frame.SourceFile)}:{frame.Line} в {frame.Function}";

	private static string ShortFile(string? file) =>
		string.IsNullOrEmpty(file) ? "<нет файла>" : Path.GetFileName(file);

	private static string Format(KValue value) =>
		value.Kind == ValueKind.None ? "<none>" : value.ToString() ?? value.TypeName;
}
