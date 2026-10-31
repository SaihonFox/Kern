using System.Text;

namespace Kern.Vm.Execution;

/// <summary>
/// Kern-panic: ошибка времени выполнения с русским сообщением и стек-трейсом функций VM.
/// Трассировка заполняется интерпретатором при раскрутке кадров (изнутри наружу).
/// </summary>
public sealed class KernPanic : Exception
{
	/// <summary>Кадры стека: «функция (строка N)», от внутреннего к внешнему.</summary>
	public List<string> Frames { get; } = [];

	public KernPanic(string message) : base(message)
	{
	}

	public KernPanic(string message, Exception? innerException) : base(message, innerException)
	{
	}

	/// <summary>Добавить кадр трассировки; возвращает этот же объект для цепочек.</summary>
	public KernPanic AddFrame(string function, int offset, int? line)
	{
		Frames.Add(line is null ? $"{function} (смещение {offset})" : $"{function} (строка {line})");
		return this;
	}

	/// <summary>Готовый текст: сообщение и стек-трейс.</summary>
	public string Describe()
	{
		StringBuilder sb = new();
		sb.Append("panic: ").Append(Message);
		foreach (string frame in Frames) sb.Append('\n').Append("  в ").Append(frame);
		return sb.ToString();
	}
}
