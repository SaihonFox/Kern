using Kern.Bytecode;
using Kern.Vm.Execution;

namespace Kern.Vm.Values;

/// <summary>
/// Источник элементов ленивой последовательности. Реализуется генератором VM и
/// ленивыми обёртками <c>kern.std</c> (<c>map</c>/<c>filter</c>/<c>take</c>/<c>skip</c>).
/// </summary>
public interface ISeqSource
{
	/// <summary>Получить следующий элемент; <c>false</c> — последовательность закончилась.</summary>
	bool TryNext(out KValue value);
}

/// <summary>
/// Ленивая последовательность <c>seq&lt;T&gt;</c> (spec §6, раунд 27): генератор не
/// исполняется до первого <c>next()</c>, каждый <c>yield</c> отдаёт один элемент.
/// </summary>
public sealed class KSeq
{
	private readonly Interpreter? _interpreter;
	private readonly FunctionDef? _function;
	private readonly KValue[]? _arguments;
	private readonly KValue[]? _captures;
	private readonly ISeqSource? _source;
	private Interpreter.KGenerator? _generator;

	public KSeq(Interpreter interpreter, FunctionDef function, KValue[] arguments, KValue[]? captures = null)
	{
		_interpreter = interpreter;
		_function = function;
		_arguments = arguments;
		_captures = captures;
	}

	public KSeq(ISeqSource source) => _source = source;

	/// <summary>Следующий элемент или <c>false</c> в конце.</summary>
	public bool TryNext(out KValue value)
	{
		if (_source is not null)
			return _source.TryNext(out value);

		_generator ??= _interpreter!.CreateGenerator(_function!, _arguments!, _captures);
		return _generator.TryNext(out value);
	}
}
