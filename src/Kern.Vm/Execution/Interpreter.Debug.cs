using Kern.Bytecode;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>Снимок состояния VM для отладчика (<see cref="IDebugHook"/>).</summary>
public sealed partial class Interpreter
{
	private static DebugPauseInfo CreateDebugPause(
		FunctionDef function,
		int offset,
		KValue[] locals,
		Frame[] frames,
		int frameCount)
	{
		var stack = new List<DebugFrameInfo>(frameCount + 1)
		{
			FrameInfo(function, offset, locals),
		};
		for (int index = frameCount - 1; index >= 0; index--)
		{
			Frame caller = frames[index];
			stack.Add(FrameInfo(caller.Function, caller.Pc, caller.Locals));
		}

		return new DebugPauseInfo
		{
			Function = function.Name,
			SourceFile = function.SourceFile,
			Line = function.LineAt(offset) ?? 0,
			Offset = offset,
			FrameCount = frameCount,
			Stack = stack,
			Locals = locals,
			ParamCount = function.ParamCount,
		};
	}

	private static DebugFrameInfo FrameInfo(FunctionDef function, int offset, KValue[] locals) =>
		new()
		{
			Function = function.Name,
			SourceFile = function.SourceFile,
			Line = function.LineAt(offset) ?? 0,
			Offset = offset,
			Locals = locals,
			ParamCount = function.ParamCount,
		};
}
