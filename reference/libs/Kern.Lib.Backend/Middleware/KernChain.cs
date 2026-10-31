using Kern.Binding.Attributes;
using Kern.Lib.Backend.Http;

namespace Kern.Lib.Backend.Middleware;

internal delegate KernRes Terminal(KernCtx ctx);
internal delegate KernRes Layer(KernCtx ctx, KernChain next);

/// <summary>Остаток конвейера: `n.next()` вызывает следующий middleware или сам обработчик.</summary>
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

	[KernMember]
	public KernRes Next() =>
		_index < _layers.Count ? _layers[_index](_ctx, new KernChain(_layers, _terminal, _ctx, _index + 1)) : _terminal(_ctx);
}
