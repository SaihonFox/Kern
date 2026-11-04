using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kern.Bytecode;
using Kern.Vm.Execution;

namespace Kern.Cli.Debug;

/// <summary>
/// Отладчик по протоколу DAP (Debug Adapter Protocol) поверх TCP: <c>kernc debug --dap
/// &lt;порт&gt; ...</c>. Клиент (VS Code, IDE или тест) подключается к loopback-порту и
/// управляет точками останова, шагом, стеком, локальными и выражениями. Сервер живёт в
/// фоновом потоке, поэтому вывод программы не смешивается с сообщениями DAP.
/// </summary>
internal sealed class DapDebugger : IDebugHook, IDisposable
{
	private readonly TcpListener _listener;
	private readonly ManualResetEventSlim _clientReady = new(false);
	private readonly ManualResetEventSlim _configured = new(false);
	private readonly ManualResetEventSlim _resume = new(false);
	private readonly object _writeLock = new();
	private readonly object _stateLock = new();
	private readonly Dictionary<string, HashSet<int>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _functionBreakpoints = new(StringComparer.Ordinal);
	private NetworkStream? _stream;
	private Thread? _reader;
	private DebugPauseInfo? _current;
	private volatile bool _abort;
	private Mode _mode = Mode.None;
	private int _depth;
	private bool _stopOnEntry;
	private bool _entry = true;
	private (string Function, int Line)? _resumed;
	private int _sequence;

	private enum Mode
	{
		None,
		Step,
		Next,
		Finish,
	}

	public DapDebugger(int port)
	{
		_listener = new TcpListener(IPAddress.Loopback, port);
		_listener.Start();
		AcceptClient();
		// Клиент DAP сначала присылает initialize/launch/setBreakpoints; программу
		// запускаем только после configurationDone, иначе она успеет завершиться.
		_configured.Wait(TimeSpan.FromSeconds(30));
	}

	public bool ShouldPause(FunctionDef function, int offset, int? line, int frameCount)
	{
		if (_abort)
			return false;
		if (_entry && string.Equals(function.Name, "main", StringComparison.Ordinal))
		{
			_entry = false;
			if (_stopOnEntry)
				return true;
		}

		if (_functionBreakpoints.Contains(function.Name))
			return true;
		if (_mode == Mode.Step)
			return true;
		if (_mode == Mode.Next && frameCount <= _depth)
			return true;
		if (_mode == Mode.Finish && frameCount < _depth)
			return true;
		if (_resumed is { } resumed &&
			string.Equals(resumed.Function, function.Name, StringComparison.Ordinal))
		{
			if (line == resumed.Line)
				return false;
			_resumed = null;
		}

		lock (_stateLock)
		{
			if (_breakpoints.Count == 0 || line is null)
				return false;
			foreach (var (file, lines) in _breakpoints)
			{
				if (!lines.Contains(line.Value))
					continue;
				if (file.Length == 0 ||
					string.Equals(Path.GetFileName(file), Path.GetFileName(function.SourceFile), StringComparison.OrdinalIgnoreCase) ||
					string.Equals(file, function.SourceFile, StringComparison.OrdinalIgnoreCase))
					return true;
			}
		}

		return false;
	}

	public bool Pause(DebugPauseInfo pause)
	{
		bool entry = _entry;
		_entry = false;
		_mode = Mode.None;
		_resumed = null;
		if (!_clientReady.Wait(TimeSpan.FromSeconds(30)) || _stream is null)
			return !_abort;

		lock (_stateLock)
			_current = pause;
		_resume.Reset();
		SendEvent("stopped", new JsonObject
		{
			["reason"] = entry ? "entry" : "breakpoint",
			["threadId"] = 1,
			["allThreadsStopped"] = true,
		});
		while (!_resume.IsSet)
		{
			if (_abort)
				return false;
			_resume.Wait(100);
		}
		_resume.Reset();
		lock (_stateLock)
			_current = null;
		return !_abort;
	}

	private void AcceptClient()
	{
		try
		{
			TcpClient client = _listener.AcceptTcpClient();
			client.NoDelay = true;
			_stream = client.GetStream();
			_clientReady.Set();
			_reader = new Thread(ReadLoop) { IsBackground = true, Name = "kern-dap-reader" };
			_reader.Start();
		}
		catch (SocketException)
		{
			_abort = true;
		}
		catch (ObjectDisposedException)
		{
			_abort = true;
		}
	}

	private void ReadLoop()
	{
		try
		{
			while (!_abort && _stream is not null)
			{
				JsonNode? message = ReadMessage(_stream);
				if (message is null)
					break;
				if (!string.Equals((string?)message["type"], "request", StringComparison.Ordinal))
					continue;
				HandleRequest(message);
			}
		}
		catch (IOException)
		{
		}
		catch (ObjectDisposedException)
		{
		}
		finally
		{
			_abort = true;
			_resume.Set();
		}
	}

	private void HandleRequest(JsonNode request)
	{
		int sequence = (int?)request["seq"] ?? 0;
		string command = (string?)request["command"] ?? "";
		JsonNode? arguments = request["arguments"];

		switch (command)
		{
			case "initialize":
				Respond(sequence, command, new JsonObject
				{
					["supportsConfigurationDoneRequest"] = true,
					["supportsEvaluateForHovers"] = true,
					["supportsSetVariable"] = false,
					["supportsFunctionBreakpoints"] = true,
				});
				break;
			case "launch":
			case "attach":
				_stopOnEntry = (bool?)arguments?["stopOnEntry"] ?? false;
				Respond(sequence, command);
				SendEvent("initialized", new JsonObject());
				break;
			case "configurationDone":
				Respond(sequence, command);
				_configured.Set();
				break;
			case "setBreakpoints":
				SetBreakpoints(arguments);
				Respond(sequence, command, new JsonObject { ["breakpoints"] = new JsonArray() });
				break;
			case "setFunctionBreakpoints":
				lock (_stateLock)
				{
					_functionBreakpoints.Clear();
					foreach (JsonNode? item in (arguments?["breakpoints"] as JsonArray) ?? [])
						if ((string?)item?["name"] is { Length: > 0 } name)
							_functionBreakpoints.Add(name);
				}
				Respond(sequence, command, new JsonObject { ["breakpoints"] = new JsonArray() });
				break;
			case "setExceptionBreakpoints":
				Respond(sequence, command);
				break;
			case "threads":
				Respond(sequence, command, new JsonObject
				{
					["threads"] = new JsonArray(new JsonObject
					{
						["id"] = 1,
						["name"] = "main",
					}),
				});
				break;
			case "stackTrace":
				Respond(sequence, command, StackTrace());
				break;
			case "scopes":
				Respond(sequence, command, new JsonObject
				{
					["scopes"] = new JsonArray(new JsonObject
					{
						["name"] = "Локальные",
						["variablesReference"] = 1,
						["expensive"] = false,
					}),
				});
				break;
			case "variables":
				Respond(sequence, command, Variables());
				break;
			case "evaluate":
				Evaluate(sequence, arguments);
				break;
			case "continue":
				Resume(Mode.None, sequence, command);
				break;
			case "next":
			case "stepIn":
				Resume(Mode.Step, sequence, command);
				break;
			case "stepOut":
				Resume(Mode.Finish, sequence, command);
				break;
			case "pause":
				Respond(sequence, command);
				break;
			case "disconnect":
			case "terminate":
				Respond(sequence, command);
				_abort = true;
				_resume.Set();
				break;
			default:
				Respond(sequence, command);
				break;
		}
	}

	private void SetBreakpoints(JsonNode? arguments)
	{
		string path = (string?)arguments?["source"]?["path"] ?? "";
		var lines = new HashSet<int>();
		foreach (JsonNode? item in (arguments?["breakpoints"] as JsonArray) ?? [])
			if ((int?)item?["line"] is { } line)
				lines.Add(line);
		lock (_stateLock)
		{
			if (lines.Count == 0)
				_breakpoints.Remove(path);
			else
				_breakpoints[path] = lines;
		}
	}

	private void Resume(Mode mode, int sequence, string command)
	{
		lock (_stateLock)
		{
			_mode = mode;
			_depth = _current?.FrameCount ?? 0;
			_resumed = mode == Mode.None && _current is { } current
				? (current.Function, current.Line)
				: null;
		}
		Respond(sequence, command);
		_resume.Set();
	}

	private void Evaluate(int sequence, JsonNode? arguments)
	{
		string expression = (string?)arguments?["expression"] ?? "";
		DebugPauseInfo? pause;
		lock (_stateLock)
			pause = _current;
		if (pause is null)
		{
			Respond(sequence, "evaluate", success: false, message: "программа не остановлена");
			return;
		}

		if (DebugExpression.TryEvaluate(expression, pause, out Vm.Values.KValue value, out string? error))
		{
			Respond(sequence, "evaluate", new JsonObject
			{
				["result"] = Format(value),
				["type"] = value.TypeName,
				["variablesReference"] = 0,
			});
			return;
		}

		Respond(sequence, "evaluate", success: false, message: error ?? "ошибка выражения");
	}

	private JsonObject StackTrace()
	{
		DebugPauseInfo? pause;
		lock (_stateLock)
			pause = _current;
		var frames = new JsonArray();
		if (pause is null)
			return new JsonObject { ["stackFrames"] = frames };
		for (var index = 0; index < pause.Stack.Count; index++)
		{
			DebugFrameInfo frame = pause.Stack[index];
			frames.Add(new JsonObject
			{
				["id"] = index,
				["name"] = frame.Function,
				["line"] = frame.Line,
				["column"] = 1,
				["source"] = new JsonObject
				{
					["name"] = Path.GetFileName(frame.SourceFile),
					["path"] = frame.SourceFile,
				},
			});
		}

		return new JsonObject { ["stackFrames"] = frames };
	}

	private JsonObject Variables()
	{
		DebugPauseInfo? pause;
		lock (_stateLock)
			pause = _current;
		var variables = new JsonArray();
		if (pause is not null)
			for (var slot = 0; slot < pause.Locals.Count; slot++)
			{
				string name = slot < pause.LocalNames.Count && pause.LocalNames[slot].Length > 0
					? pause.LocalNames[slot]
					: slot < pause.ParamCount ? $"param{slot}" : $"slot{slot}";
				Vm.Values.KValue value = pause.Locals[slot];
				variables.Add(new JsonObject
				{
					["name"] = name,
					["value"] = Format(value),
					["type"] = value.TypeName,
					["variablesReference"] = 0,
				});
			}

		return new JsonObject { ["variables"] = variables };
	}

	private void SendEvent(string name, JsonObject body)
	{
		Send(new JsonObject
		{
			["seq"] = Interlocked.Increment(ref _sequence),
			["type"] = "event",
			["event"] = name,
			["body"] = body,
		});
	}

	private void Respond(int requestSequence, string command, JsonObject? body = null)
	{
		var message = new JsonObject
		{
			["seq"] = Interlocked.Increment(ref _sequence),
			["type"] = "response",
			["request_seq"] = requestSequence,
			["success"] = true,
			["command"] = command,
		};
		if (body is not null)
			message["body"] = body;
		Send(message);
	}

	private void Respond(int requestSequence, string command, bool success, string message)
	{
		Send(new JsonObject
		{
			["seq"] = Interlocked.Increment(ref _sequence),
			["type"] = "response",
			["request_seq"] = requestSequence,
			["success"] = success,
			["command"] = command,
			["message"] = message,
		});
	}

	private void Send(JsonObject message)
	{
		if (_stream is null)
			return;
		byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
		lock (_writeLock)
		{
			try
			{
				byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
				_stream.Write(header);
				_stream.Write(body);
				_stream.Flush();
			}
			catch (IOException)
			{
				_abort = true;
			}
			catch (ObjectDisposedException)
			{
				_abort = true;
			}
		}
	}

	private static JsonNode? ReadMessage(Stream stream)
	{
		int length = -1;
		while (true)
		{
			string? line = ReadLine(stream);
			if (line is null)
				return null;
			if (line.Length == 0)
				break;
			if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
				int.TryParse(line["Content-Length:".Length..].Trim(), out int parsed))
				length = parsed;
		}

		if (length < 0)
			return null;
		var body = new byte[length];
		var read = 0;
		while (read < length)
		{
			int chunk = stream.Read(body, read, length - read);
			if (chunk <= 0)
				return null;
			read += chunk;
		}

		return JsonNode.Parse(body);
	}

	private static string? ReadLine(Stream stream)
	{
		var builder = new StringBuilder();
		while (true)
		{
			int value = stream.ReadByte();
			if (value < 0)
				return null;
			if (value == '\n')
				return builder.ToString().TrimEnd('\r');
			builder.Append((char)value);
		}
	}

	private static string Format(Vm.Values.KValue value) =>
		value.Kind == Vm.Values.ValueKind.None ? "<none>" : value.ToString() ?? value.TypeName;

	public void Dispose()
	{
		_abort = true;
		_resume.Set();
		_stream?.Dispose();
		_listener.Stop();
		_clientReady.Dispose();
		_configured.Dispose();
		_resume.Dispose();
	}
}
