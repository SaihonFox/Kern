using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;

namespace Kern.Compiler.CodeGen;

/// <summary>
/// Хвостовая саморекурсия (spec §6, раунд 27): прямой вызов текущей функции в позиции
/// возврата компилируется в <see cref="OpCode.TailCall"/> и не наращивает стек VM.
/// Хвосты <c>if</c>/<c>match</c> и блоков поддерживаются рекурсивно; взаимная рекурсия,
/// виртуальные вызовы и async-функции не оптимизируются.
/// </summary>
public sealed partial class CodeGenerator
{
	/// <summary>Можно ли вернуть значение этой ветви хвостовым вызовом (без потери значения).</summary>
	private bool CanTail(BoundExpression expression) => expression switch
	{
		BoundCallExpression call => IsSelfRecursive(call),
		BoundIfExpression conditional => CanTail(conditional.Then) ||
			(conditional.Else is not null && CanTail(conditional.Else)),
		BoundMatchExpression match => match.Arms.Any(arm => CanTail(arm.Body)),
		BoundBlockExpression block => block.Value is not null && CanTail(block.Value),
		_ => false,
	};

	/// <summary>
	/// Прямая саморекурсия: та же функция, не виртуальный вызов, не конструктор и не async.
	/// Нативные функции не имеют байткода и в хвостовой вызов не попадают.
	/// </summary>
	private bool IsSelfRecursive(BoundCallExpression call) =>
		_currentSymbol is not null &&
		ReferenceEquals(call.Function, _currentSymbol) &&
		!call.Virtual &&
		!_current.IsNative &&
		(_current.Flags & (MemberFlags.Async | MemberFlags.Ctor)) == 0 &&
		_functionIndices.ContainsKey(call.Function);

	/// <summary>
	/// Сгенерировать выражение в позиции возврата: если оно (или его ветвь) — хвостовой
	/// саморекурсивный вызов, печатается <c>TailCall</c>, иначе обычное выражение.
	/// </summary>
	private void EmitTail(BoundExpression expression)
	{
		switch (expression)
		{
			case BoundCallExpression call:
				EmitTailCall(call);
				break;

			case BoundIfExpression conditional:
				EmitIf(conditional, tail: true);
				break;

			case BoundMatchExpression match:
				EmitMatch(match, tail: true);
				break;

			case BoundBlockExpression block:
				EmitBlock(
					new BoundBlock(block.Statements, block.Value, block.FallsThrough, block.Span),
					leaveValue: block.FallsThrough || block.Value is not null,
					tailPosition: true);
				break;

			default:
				EmitExpression(expression);
				break;
		}
	}

	/// <summary>
	/// Аргументы вычисляются как у обычного вызова, затем освобождаются <c>use</c>-кадры
	/// текущей активации и управление уходит в начало той же функции.
	/// </summary>
	private void EmitTailCall(BoundCallExpression call)
	{
		if (call.Receiver is not null)
			EmitExpression(call.Receiver);
		foreach (var argument in call.Arguments)
		{
			EmitExpression(argument);
			EmitCopyIfUnit(argument.Type);
		}

		EmitDisposalsThrough(0);
		Emit(OpCode.TailCall);
		EmitOperand(_functionIndices[call.Function]);
		EmitOperand(call.Arguments.Count + (call.Receiver is null ? 0 : 1));
	}

	/// <summary>Ветвь <c>if</c>/<c>match</c>: хвостовой вызов, если он возможен, иначе значение.</summary>
	private void EmitTailBranch(BoundExpression branch, bool tail)
	{
		if (tail && CanTail(branch))
			EmitTail(branch);
		else
			EmitExpression(branch);
	}
}
