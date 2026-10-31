using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.CodeGen;

/// <summary>
/// use/dispose (spec §12): <c>use T x = expr</c> объявляет локальную переменную и
/// регистрирует <c>x.dispose()</c> на выходе из блока. Кадры очистки повторяют
/// лексическую вложенность блоков: <see cref="EmitBlock"/> открывает кадр, обычное
/// завершение блока печатает вызовы в обратном порядке объявления, <c>ret</c>
/// освобождает все кадры функции, <c>stop</c>/<c>next</c> — кадры до тела ближайшего
/// цикла включительно. Паника очистку не выполняет (spec §12).
/// </summary>
public sealed partial class CodeGenerator
{
	private readonly List<DisposeFrame> _disposeFrames = [];
	private readonly List<int> _loopDisposeFrames = [];

	private sealed class DisposeFrame
	{
		public List<DisposeAction> Actions { get; } = [];
	}

	private readonly record struct DisposeAction(int Slot, FunctionSymbol Dispose, TextSpan Span);

	/// <summary>Открыть кадр очистки; <paramref name="loopBody"/> помечает кадр тела цикла.</summary>
	private void PushDisposeFrame(bool loopBody)
	{
		_disposeFrames.Add(new DisposeFrame());
		if (loopBody)
		{
			_loopDisposeFrames.Add(_disposeFrames.Count - 1);
			// Тело цикла открывается сразу после push его контекста: запоминаем кадр,
			// чтобы stop/next с меткой могли освободить именно до целевого цикла.
			if (_loops.Count > 0)
				_loops.Peek().DisposeFrameIndex = _disposeFrames.Count - 1;
		}
	}

	/// <summary>Закрыть кадр; маркер тела цикла снимается вместе со своим кадром.</summary>
	private void PopDisposeFrame(bool loopBody)
	{
		_disposeFrames.RemoveAt(_disposeFrames.Count - 1);
		if (loopBody)
			_loopDisposeFrames.RemoveAt(_loopDisposeFrames.Count - 1);
	}

	/// <summary><c>use x = expr</c>: сохранить значение в слот и запомнить dispose текущего блока.</summary>
	private void EmitUse(BoundUseStatement use)
	{
		EmitExpression(use.Initializer);
		EmitCopyIfUnit(use.Type);
		Emit(OpCode.StoreLocal, use.Slot);

		if (use.Dispose is not null && _disposeFrames.Count > 0)
			_disposeFrames[^1].Actions.Add(new DisposeAction(use.Slot, use.Dispose, use.Span));
	}

	/// <summary>
	/// Вызовы <c>dispose()</c> от самого внутреннего кадра до <paramref name="targetIndex"/>
	/// включительно, внутри кадра — в обратном порядке объявления.
	/// </summary>
	private void EmitDisposalsThrough(int targetIndex)
	{
		for (var frame = _disposeFrames.Count - 1; frame >= targetIndex && frame >= 0; frame--)
		{
			var actions = _disposeFrames[frame].Actions;
			for (var index = actions.Count - 1; index >= 0; index--)
			{
				DisposeAction action = actions[index];
				Emit(OpCode.LoadLocal, action.Slot);
				EmitCallTo(action.Dispose, argumentCount: 1, action.Span);
				Emit(OpCode.Pop);
			}
		}
	}
}
