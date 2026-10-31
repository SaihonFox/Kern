using Kern.Bytecode;

namespace Kern.Vm.Values;

/// <summary>Замыкание: функция плюс захваченные значения (по значению).</summary>
public sealed class KClosure
{
	public KClosure(FunctionDef function, KValue[] captures)
	{
		Function = function ?? throw new ArgumentNullException(nameof(function));
		Captures = captures ?? throw new ArgumentNullException(nameof(captures));
	}

	public FunctionDef Function { get; }

	public KValue[] Captures { get; }

	public override string ToString() => $"closure({Function.Name})";
}
