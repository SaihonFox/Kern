using System.Text;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>Строка Kern-стека: функция + строка исходника.</summary>
public sealed record KernStackFrame(string Function, string? File, int Line)
{
	public override string ToString() => Line > 0 ? $"at {Function} ({File ?? "?"}:{Line})" : $"at {Function} ({File ?? "?"})";
}

/// <summary>panic Kern. Несёт Kern-стек (заполняется интерпретатором при размотке).</summary>
public sealed class KernPanic : Exception
{
	public KernPanic(string message, Exception? inner = null) : base(message, inner) { }
	public List<KernStackFrame> Trace { get; set; } = [];
	public bool TraceFilled { get; set; }
	/// <summary>Значение, переданное в panic(...), если оно не строка.</summary>
	public KValue Payload { get; set; }

	/// <summary>Красивый вывод: сообщение и стек.</summary>
	public string Format()
	{
		var sb = new StringBuilder();
		sb.Append("panic: ").AppendLine(Message);
		foreach (var f in Trace) sb.Append("  ").AppendLine(f.ToString());
		return sb.ToString().TrimEnd();
	}
}

/// <summary>Запрос завершения программы с кодом (нативный exit(n)).</summary>
public sealed class KernExit(int code) : Exception($"exit {code}")
{
	public int Code { get; } = code;
}
