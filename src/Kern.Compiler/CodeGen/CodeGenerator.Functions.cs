using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;

namespace Kern.Compiler.CodeGen;

/// <summary>
/// Генерация функции и блока: пролог (в т.ч. упаковка захваченных параметров в ячейки),
/// тело, эпилог возврата; блок открывает кадр очистки <c>use</c>.
/// </summary>
public sealed partial class CodeGenerator
{
	private void EmitFunction(BoundFunction function)
	{
		_current = _module.FunctionPool[_functionIndices[function.Symbol]];
		_code = [];
		_current.LineTable = [];
		if (_bundle is not null)
			_current.SourceFile = SourceFileFor(function, _bundle);
		else if (!string.IsNullOrEmpty(_source?.Path))
			_current.SourceFile = _source.Path;

		_boxedSlots = function.BoxedSlots;
		foreach (var parameter in function.Parameters)
		{
			if (!_boxedSlots.Contains(parameter.Slot))
				continue;
			Emit(OpCode.LoadLocal, parameter.Slot);
			Emit(OpCode.NewCell);
			Emit(OpCode.StoreLocal, parameter.Slot);
		}

		EmitConstructorEntry(function);
		var body = function.Body;
		var leavesValue = body.Value is not null;
		EmitBlock(body, leavesValue);

		if (IsVoid(function.ReturnType))
		{
			if (leavesValue)
				Emit(OpCode.Pop);
			Emit(OpCode.PushVoid);
			Emit(OpCode.Ret);
		}
		else if (leavesValue)
		{
			Emit(OpCode.Ret);
		}
		else if (body.FallsThrough)
		{
			Emit(OpCode.PushVoid);
			Emit(OpCode.Ret);
		}

		_current.Code = [.. _code];
	}

	private void EmitBlock(BoundBlock block, bool leaveValue, bool loopBody = false)
	{
		PushDisposeFrame(loopBody);
		try
		{
			var valueEmittedByTail = false;
			for (var index = 0; index < block.Statements.Count; index++)
			{
				var statement = block.Statements[index];
				var isTailValue = leaveValue &&
					block.Value is not null &&
					index == block.Statements.Count - 1 &&
					statement is BoundExpressionStatement expressionStatement &&
					ReferenceEquals(expressionStatement.Expression, block.Value);
				EmitStatement(statement, isTailValue);
				valueEmittedByTail |= isTailValue;
			}

			// Блок-выражение из одного выражения (например, accessor `get $x * 2`) не имеет statements:
			// значение нужно сгенерировать явно, иначе функция вернёт пустоту.
			if (leaveValue && block.Value is not null && !valueEmittedByTail)
				EmitExpression(block.Value);

			// use: обычный выход из блока освобождает переменные в обратном порядке объявления.
			if (block.FallsThrough)
				EmitDisposalsThrough(_disposeFrames.Count - 1);

			if (leaveValue && block.Value is null && block.FallsThrough)
				Emit(OpCode.PushVoid);
		}
		finally
		{
			PopDisposeFrame(loopBody);
		}
	}
}
