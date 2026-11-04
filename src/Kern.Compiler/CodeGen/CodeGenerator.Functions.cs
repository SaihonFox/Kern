using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;

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
		_currentSymbol = function.Symbol;
		_code = [];
		_current.LineTable = [];
		_current.LocalNames = [.. Enumerable.Repeat(string.Empty, _current.LocalCount)];
		foreach (var parameter in function.Parameters)
			RecordLocalName(parameter.Slot, parameter.Symbol.Name);
		if (_bundle is not null)
			_current.SourceFile = SourceFileFor(function, _bundle);
		else if (!string.IsNullOrEmpty(_source?.Path))
			_current.SourceFile = _source.Path;

		_boxedSlots = function.BoxedSlots;
		_refSlots = function.Parameters
			.Where(parameter => parameter.Symbol.Passing == ParameterPassing.Ref)
			.Select(parameter => parameter.Slot)
			.ToHashSet();
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
		// Хвостовая саморекурсия — только у функций, возвращающих значение (не async/ctor:
		// это проверяет IsSelfRecursive).
		EmitBlock(body, leavesValue, tailPosition: leavesValue && !IsVoid(function.ReturnType));

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
		if (_current.LocalNames.Count < _current.LocalCount)
			_current.LocalNames.AddRange(
				Enumerable.Repeat(string.Empty, _current.LocalCount - _current.LocalNames.Count));
	}

	/// <summary>Запомнить имя локального слота для отладчика (пустые/временные слоты — "").</summary>
	private void RecordLocalName(int slot, string name)
	{
		if (slot < 0 || name.Length == 0)
			return;
		while (_current.LocalNames.Count <= slot)
			_current.LocalNames.Add(string.Empty);
		_current.LocalNames[slot] = name;
	}

	private void EmitBlock(
		BoundBlock block,
		bool leaveValue,
		bool loopBody = false,
		bool tailPosition = false)
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
				if (isTailValue &&
					tailPosition &&
					statement is BoundExpressionStatement tailStatement &&
					CanTail(tailStatement.Expression))
				{
					EmitTail(tailStatement.Expression);
				}
				else
				{
					EmitStatement(statement, isTailValue);
				}

				valueEmittedByTail |= isTailValue;
			}

			// Блок-выражение из одного выражения (например, accessor `get $x * 2`) не имеет statements:
			// значение нужно сгенерировать явно, иначе функция вернёт пустоту.
			if (leaveValue && block.Value is not null && !valueEmittedByTail)
			{
				if (tailPosition && CanTail(block.Value))
					EmitTail(block.Value);
				else
					EmitExpression(block.Value);
			}

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
