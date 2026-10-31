using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.CodeGen;

/// <summary>Вызовы и создание объектов (spec §4/§8): Call/CallVirtual/CallBase и NewObject.</summary>
public sealed partial class CodeGenerator
{
	private void EmitCall(BoundCallExpression call)
	{
		if (call.Receiver is not null)
			EmitExpression(call.Receiver);
		foreach (var argument in call.Arguments)
		{
			EmitExpression(argument);
			EmitCopyIfUnit(argument.Type);
		}

		if (!_functionIndices.TryGetValue(call.Function, out var index))
		{
			Report("KRN2201", $"Функция '{call.Function.Name}' не имеет байткода (нативная привязка появится в M6)", call.Span);
			for (var argument = 0; argument < call.Arguments.Count; argument++)
				Emit(OpCode.Pop);
			if (call.Receiver is not null)
				Emit(OpCode.Pop);
			Emit(OpCode.PushVoid);
			return;
		}

		var argumentCount = call.Arguments.Count + (call.Receiver is null ? 0 : 1);
		Emit(call.Virtual ? OpCode.CallVirtual : OpCode.Call);
		EmitOperand(index);
		EmitOperand(argumentCount);
	}

	private void EmitObjectCreation(BoundObjectCreationExpression creation)
	{
		if (!_typeIndices.TryGetValue(creation.TypeSymbol, out var typeIndex))
		{
			Report("KRN2201", $"Тип '{creation.TypeSymbol.Name}' не попал в TypePool (M3-D)", creation.Span);
			Emit(OpCode.PushVoid);
			return;
		}

		// Для generic-типа сохраняем reified-аргументы: иначе `is`/`match` по `Some<i32>`
		// не совпадёт (NewObject создаёт тип без аргументов).
		IReadOnlyList<KernType> typeArguments = creation.Type switch
		{
			ClassType { Arguments.Count: > 0 } @class => @class.Arguments,
			UnitType { Arguments.Count: > 0 } unit => unit.Arguments,
			_ => []
		};
		if (typeArguments.Count == 0)
		{
			Emit(OpCode.NewObject, typeIndex);
		}
		else
		{
			var signature = _module.InternTypeSigList(
				typeArguments.Select(argument => TypeSigMap.From(argument, _module.Name)));
			Emit(OpCode.NewObjectGeneric, typeIndex);
			EmitOperand(signature);
		}

		if (!_functionIndices.TryGetValue(creation.Constructor, out var constructorIndex))
		{
			Report("KRN2201", $"Конструктор типа '{creation.TypeSymbol.Name}' не найден (M3-D)", creation.Span);
			return;
		}

		Emit(OpCode.Dup);
		foreach (var argument in creation.Arguments)
		{
			EmitExpression(argument);
			EmitCopyIfUnit(argument.Type);
		}
		Emit(OpCode.Call);
		EmitOperand(constructorIndex);
		EmitOperand(creation.Arguments.Count + 1);
		Emit(OpCode.Pop);
	}

}
