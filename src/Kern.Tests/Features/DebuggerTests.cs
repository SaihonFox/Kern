using Kern.Binding.Resolution;
using Kern.Bytecode;
using Kern.Cli;
using Kern.Cli.Debug;
using Kern.Tests.Features.Support;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Отладчик (docs/DEBUGGER.md): VM-хук <see cref="IDebugHook"/>, интерактивные команды
/// <see cref="ConsoleDebugger"/> и команда <c>kernc debug</c>.
/// </summary>
[Collection("timing")]
public sealed class DebuggerTests
{
	private const string Source = """
		fun add(i32 a, i32 b) -> i32 a + b

		fun main() -> i32 {
			i32 x = add(2, 3)
			i32 y = x * 2
			y
		}
		""";

	[Fact]
	public void Debug_hook_pauses_on_a_function_and_sees_parameters_and_stack()
	{
		var hook = new TestHook("add");
		var interpreter = new Interpreter(
			StdPipeline.Compile(Source).Module,
			new NativeResolver(StdPipeline.Catalog()))
		{
			Debugger = hook,
		};

		KValue result = interpreter.Run("main");

		Assert.Equal(KValue.I32(10), result);
		Assert.Equal(1, hook.Pauses);
		Assert.NotNull(hook.Last);
		Assert.Equal(KValue.I32(2), hook.Last!.Locals[0]);
		Assert.Equal(KValue.I32(3), hook.Last.Locals[1]);
		Assert.Contains(hook.Last.Stack, frame => frame.Function == "main");
	}

	[Fact]
	public void Debug_hook_can_abort_execution()
	{
		var hook = new TestHook("main") { Stop = true };
		var interpreter = new Interpreter(
			StdPipeline.Compile(Source).Module,
			new NativeResolver(StdPipeline.Catalog()))
		{
			Debugger = hook,
		};

		Assert.Throws<DebugAbortedException>(() => interpreter.Run("main"));
	}

	[Fact]
	public void Local_names_survive_kbc_roundtrip_and_reach_the_debugger()
	{
		KbcModule module = StdPipeline.Compile(Source).Module;
		KbcModule stored = KbcReader.Read(KbcWriter.Write(module));

		FunctionDef add = stored.FunctionPool.First(function => function.Name == "add");
		Assert.Equal(["a", "b"], add.LocalNames);
		FunctionDef main = stored.FunctionPool.First(function => function.Name == "main");
		Assert.Contains("x", main.LocalNames);
		Assert.Contains("y", main.LocalNames);

		var hook = new TestHook("add");
		var interpreter = new Interpreter(stored, new NativeResolver(StdPipeline.Catalog()))
		{
			Debugger = hook,
		};
		Assert.Equal(KValue.I32(10), interpreter.Run("main"));
		Assert.Equal(["a", "b"], hook.Last!.LocalNames);
	}

	[Fact]
	public void Console_debugger_prints_locals_by_name()
	{
		var output = new StringWriter();
		var debugger = new ConsoleDebugger(
			new StringReader("locals\nprint value\ncontinue\n"),
			output);
		var pause = new DebugPauseInfo
		{
			Function = "main",
			SourceFile = "main.kern",
			Line = 3,
			Offset = 0,
			FrameCount = 0,
			Stack = [],
			Locals = [KValue.I32(7)],
			LocalNames = ["value"],
			ParamCount = 0,
		};

		Assert.True(debugger.Pause(pause));
		string text = output.ToString();
		Assert.Contains("value = 7", text);
		Assert.DoesNotContain("slot0", text);
	}

	[Fact]
	public void Console_debugger_evaluates_simple_expressions()
	{
		var output = new StringWriter();
		var debugger = new ConsoleDebugger(
			new StringReader("print value + 1\nprint value * 2 - 1\nprint \"x\" + \"y\"\ncontinue\n"),
			output);
		var pause = new DebugPauseInfo
		{
			Function = "main",
			SourceFile = "main.kern",
			Line = 3,
			Offset = 0,
			FrameCount = 0,
			Stack = [],
			Locals = [KValue.I32(7)],
			LocalNames = ["value"],
			ParamCount = 0,
		};

		Assert.True(debugger.Pause(pause));
		string text = output.ToString();
		Assert.Contains("value + 1 = 8", text);
		Assert.Contains("value * 2 - 1 = 13", text);
		Assert.Contains("\"x\" + \"y\" = xy", text);
	}

	[Fact]
	public void Console_debugger_handles_breakpoints_locals_stack_and_continue()
	{
		var output = new StringWriter();
		var debugger = new ConsoleDebugger(
			new StringReader("break 7\nbreak add\ninfo\nlocals\nstack\ncontinue\n"),
			output);
		var function = new FunctionDef
		{
			Name = "add",
			SourceFile = "main.kern",
			LineTable = [new LineEntry(0, 7)],
			LocalCount = 2,
			ParamTypes = [TypeSig.I32, TypeSig.I32],
			Code = [],
		};
		var pause = new DebugPauseInfo
		{
			Function = "main",
			SourceFile = "main.kern",
			Line = 4,
			Offset = 0,
			FrameCount = 0,
			Stack =
			[
				new DebugFrameInfo
				{
					Function = "main",
					SourceFile = "main.kern",
					Line = 4,
					Offset = 0,
					Locals = [KValue.I32(1)],
					ParamCount = 0,
				},
			],
			Locals = [KValue.I32(1)],
			ParamCount = 1,
		};

		Assert.True(debugger.Pause(pause));
		string text = output.ToString();
		Assert.Contains("точка останова 1", text);
		Assert.Contains("точка останова 2: функция add", text);
		Assert.Contains("param0 = 1", text);
		Assert.Contains("#0 main.kern:4 в main", text);

		// Точка по строке срабатывает только в исходном файле; функция — в любом.
		Assert.True(debugger.ShouldPause(function, 0, 7, 0));
		var other = new FunctionDef
		{
			Name = "other",
			SourceFile = "other.kern",
			LineTable = [new LineEntry(0, 7)],
			LocalCount = 0,
			Code = [],
		};
		Assert.False(debugger.ShouldPause(other, 0, 7, 0));

		var quit = new ConsoleDebugger(new StringReader("quit\n"), new StringWriter());
		Assert.False(quit.Pause(pause));
	}

	[Fact]
	public void Debug_command_aborts_on_quit_and_returns_130()
	{
		string file = Path.Combine(Path.GetTempPath(), "kern-debug-" + Guid.NewGuid().ToString("N") + ".kern");
		TextReader previous = Console.In;
		try
		{
			File.WriteAllText(file, Source);
			Console.SetIn(new StringReader("quit\n"));
			var stdout = new StringWriter();
			var stderr = new StringWriter();

			int code = KernCli.Run(["debug", file], stdout, stderr);

			Assert.Equal(130, code);
			Assert.Contains("остановлено", stdout.ToString());
			Assert.Contains("отладка прервана", stderr.ToString());
		}
		finally
		{
			Console.SetIn(previous);
			File.Delete(file);
		}
	}

	private sealed class TestHook(string pauseOn) : IDebugHook
	{
		public bool Stop { get; init; }

		public int Pauses { get; private set; }

		public DebugPauseInfo? Last { get; private set; }

		public bool ShouldPause(FunctionDef function, int offset, int? line, int frameCount) =>
			function.Name == pauseOn && offset == 0;

		public bool Pause(DebugPauseInfo pause)
		{
			Pauses++;
			Last = pause;
			return !Stop;
		}
	}
}
