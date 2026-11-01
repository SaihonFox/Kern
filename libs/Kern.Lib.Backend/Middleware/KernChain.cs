using Kern.Binding.Attributes;
using Kern.Lib.Backend.Http;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Middleware;

internal delegate KernRes Layer(KernCtx ctx, KernChain next);
internal delegate KernRes Terminal(KernCtx ctx);

/// <summary>Цепочка middleware: <c>n.next()</c> вызывает следующий слой или обработчик.</summary>
[KernType("chain")]
public sealed class KernChain
{
	private readonly IReadOnlyList<Layer> _layers;
	private readonly Terminal _terminal;
	private readonly KernCtx _ctx;
	private readonly int _index;

	internal KernChain(IReadOnlyList<Layer> layers, Terminal terminal, KernCtx ctx, int index = 0)
	{
		_layers = layers;
		_terminal = terminal;
		_ctx = ctx;
		_index = index;
	}

	public KernRes Next() =>
		_index < _layers.Count
			? _layers[_index](_ctx, new KernChain(_layers, _terminal, _ctx, _index + 1))
			: _terminal(_ctx);

	internal static KernChain Require(KNativeBox self) => self.Value switch
	{
		KernChain chain => chain,
		_ => throw new KernPanic($"chain: ожидалась цепочка, получено {self.Value.GetType().Name}"),
	};
}

[KernType("chain")]
public static class ChainApi
{
	[KernMember]
	public static KernRes Next(KNativeBox self) => KernChain.Require(self).Next();
}
