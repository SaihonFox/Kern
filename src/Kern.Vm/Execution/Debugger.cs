using Kern.Bytecode;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>Кадр стека в момент остановки отладчика.</summary>
public sealed class DebugFrameInfo
{
	public required string Function { get; init; }

	public string? SourceFile { get; init; }

	public int Line { get; init; }

	public int Offset { get; init; }

	public required IReadOnlyList<KValue> Locals { get; init; }

	/// <summary>Имена слотов (индекс = слот; пустая строка — имя неизвестно).</summary>
	public IReadOnlyList<string> LocalNames { get; init; } = [];

	public int ParamCount { get; init; }
}

/// <summary>Состояние VM в точке остановки: текущая инструкция и весь стек кадров.</summary>
public sealed class DebugPauseInfo
{
	public required string Function { get; init; }

	public string? SourceFile { get; init; }

	public int Line { get; init; }

	public int Offset { get; init; }

	public int FrameCount { get; init; }

	public required IReadOnlyList<DebugFrameInfo> Stack { get; init; }

	public required IReadOnlyList<KValue> Locals { get; init; }

	/// <summary>Имена слотов текущего кадра (индекс = слот).</summary>
	public IReadOnlyList<string> LocalNames { get; init; } = [];

	/// <summary>VM в точке остановки — для вычисления отладочных выражений (вызовы/поля).</summary>
	public Interpreter? Interpreter { get; init; }

	public int ParamCount { get; init; }
}

/// <summary>
/// Хук отладчика (docs/DEBUGGER.md): VM спрашивает <see cref="ShouldPause"/> перед каждой
/// инструкцией и при <c>true</c> вызывает <see cref="Pause"/>. Пока <see cref="Pause"/>
/// не вернул управление, VM стоит на месте — хук может читать локальные слоты и стек.
/// </summary>
public interface IDebugHook
{
	/// <summary>Остановиться перед инструкцией? Вызывается на каждой инструкции.</summary>
	bool ShouldPause(FunctionDef function, int offset, int? line, int frameCount);

	/// <summary>
	/// Показать состояние и прочитать команды. <c>true</c> — продолжить выполнение,
	/// <c>false</c> — прервать программу (<see cref="DebugAbortedException"/>).
	/// </summary>
	bool Pause(DebugPauseInfo pause);
}

/// <summary>Отладчик попросил завершить программу (команда <c>quit</c>).</summary>
public sealed class DebugAbortedException : Exception
{
	public DebugAbortedException() : base("отладка прервана")
	{
	}
}
