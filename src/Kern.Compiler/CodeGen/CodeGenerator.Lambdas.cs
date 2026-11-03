using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.CodeGen;

/// <summary>
/// Лямбды и функции-значения (spec §6): тело лямбды генерируется в отдельную функцию
/// пула (один раз, при первом упоминании выражения), выражение печатает
/// <c>MakeFunc</c> без захватов и <c>MakeClosure</c> с захватами. Чтение захвата —
/// <c>LoadCapture</c>, вызов значения — <c>CallValue</c>.
/// </summary>
public sealed partial class CodeGenerator
{
	/// <summary>
	/// Выражение-значение функции: сначала убедиться, что у синтезированной функции есть
	/// байткод, затем положить замыкание на стек (значения захватов вычисляются в кадре
	/// объемлющей функции, порядок совпадает со списком захватов).
	/// </summary>
	private void EmitLambda(BoundLambdaExpression lambda)
	{
		if (!_functionIndices.TryGetValue(lambda.Symbol, out var index))
		{
			Define(lambda.Function);
			EmitNestedFunction(lambda.Function);
			index = _functionIndices[lambda.Symbol];
		}

		if (lambda.Captures.Count == 0)
		{
			Emit(OpCode.MakeFunc, index);
			return;
		}

		foreach (var capture in lambda.Captures)
		{
			EmitExpression(capture.Source);
			EmitCopyIfUnit(capture.Type);
		}

		Emit(OpCode.MakeClosure, index);
		EmitOperand(lambda.Captures.Count);
	}

	/// <summary>
	/// Ссылка на функцию как значение: <c>MakeFunc</c> с индексом уже определённой
	/// функции модуля (индексы назначены до генерации тел).
	/// </summary>
	private void EmitFunctionReference(BoundFunctionReferenceExpression reference)
	{
		if (!_functionIndices.TryGetValue(reference.Symbol, out int index))
		{
			_diagnostics.Add(new Diagnostic(
				"KRN2109",
				$"функция '{reference.Symbol.Name}' недоступна как значение",
				reference.Span));
			Emit(OpCode.PushVoid);
			return;
		}

		Emit(OpCode.MakeFunc, index);
	}

	/// <summary>
	/// Вызов функции-значения: <c>CallValue argc</c> ждёт на стеке значение-функцию ниже
	/// аргументов, поэтому сначала печатается цель, потом аргументы.
	/// </summary>
	private void EmitValueCall(BoundValueCallExpression call)
	{
		EmitExpression(call.Target);
		foreach (var argument in call.Arguments)
		{
			EmitExpression(argument);
			EmitCopyIfUnit(argument.Type);
		}

		Emit(OpCode.CallValue, call.Arguments.Count);
	}

	/// <summary>
	/// Генерация вложенной функции, не входящей в <c>BoundModule.Functions</c>: состояние
	/// текущей функции (код, определение, циклы, кадры <c>use</c>) сохраняется и
	/// восстанавливается, чтобы тело лямбды не влияло на объемлющую функцию.
	/// </summary>
	private void EmitNestedFunction(BoundFunction function)
	{
		var savedCode = _code;
		var savedCurrent = _current;
		var savedSymbol = _currentSymbol;
		var savedBoxed = _boxedSlots;
		var savedRefSlots = _refSlots;
		var savedLoops = _loops.ToArray();
		var savedDisposeFrames = _disposeFrames.ToArray();
		var savedLoopDisposeFrames = _loopDisposeFrames.ToArray();
		_loops.Clear();
		_disposeFrames.Clear();
		_loopDisposeFrames.Clear();
		try
		{
			EmitFunction(function);
		}
		finally
		{
			_code = savedCode;
			_current = savedCurrent;
			_currentSymbol = savedSymbol;
			_boxedSlots = savedBoxed;
			_refSlots = savedRefSlots;
			_loops.Clear();
			for (var index = savedLoops.Length - 1; index >= 0; index--)
				_loops.Push(savedLoops[index]);
			_disposeFrames.Clear();
			_disposeFrames.AddRange(savedDisposeFrames);
			_loopDisposeFrames.Clear();
			_loopDisposeFrames.AddRange(savedLoopDisposeFrames);
		}
	}
}
