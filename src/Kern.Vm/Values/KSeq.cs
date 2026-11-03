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
/// Элементы кэшируются в общем ядре, поэтому <c>iter()</c> даёт независимый курсор и
/// повторный обход начинает последовательность сначала.
/// </summary>
public sealed class KSeq
{
	/// <summary>
	/// Общее состояние последовательности: источник/генератор, кэш уже полученных
	/// элементов и признак конца. Курсор хранится в каждом <see cref="KSeq"/>-представлении.
	/// </summary>
	private sealed class Core
	{
		public Interpreter? Interpreter;
		public FunctionDef? Function;
		public KValue[]? Arguments;
		public KValue[]? Captures;
		public ISeqSource? Source;
		public Interpreter.KGenerator? Generator;
		public readonly List<KValue> Cache = [];
		public bool Completed;
	}

	private readonly Core _core;
	private int _cursor;

	public KSeq(Interpreter interpreter, FunctionDef function, KValue[] arguments, KValue[]? captures = null) =>
		_core = new Core
		{
			Interpreter = interpreter,
			Function = function,
			Arguments = arguments,
			Captures = captures,
		};

	public KSeq(ISeqSource source) => _core = new Core { Source = source };

	private KSeq(Core core) => _core = core;

	/// <summary>Новый независимый курсор по той же последовательности (повторный обход).</summary>
	public KSeq Iter() => new(_core);

	/// <summary>Число элементов: дочитывает последовательность до конца и кэширует их.</summary>
	public int Len
	{
		get
		{
			Drain();
			return _core.Cache.Count;
		}
	}

	/// <summary>Следующий элемент или <c>false</c> в конце.</summary>
	public bool TryNext(out KValue value)
	{
		if (_cursor < _core.Cache.Count)
		{
			value = _core.Cache[_cursor++];
			return true;
		}

		if (_core.Completed || !Pull(out value))
		{
			_core.Completed = true;
			value = KValue.None;
			return false;
		}

		_core.Cache.Add(value);
		_cursor++;
		return true;
	}

	/// <summary>Дочитать источник до конца, не сдвигая курсор текущего представления.</summary>
	private void Drain()
	{
		while (!_core.Completed)
		{
			if (!Pull(out KValue value))
			{
				_core.Completed = true;
				break;
			}

			_core.Cache.Add(value);
		}
	}

	private bool Pull(out KValue value)
	{
		if (_core.Source is not null)
			return _core.Source.TryNext(out value);

		_core.Generator ??= _core.Interpreter!.CreateGenerator(_core.Function!, _core.Arguments!, _core.Captures);
		return _core.Generator.TryNext(out value);
	}
}
