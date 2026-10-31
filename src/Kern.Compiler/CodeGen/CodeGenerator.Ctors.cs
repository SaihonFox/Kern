using System.Diagnostics;
using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;

namespace Kern.Compiler.CodeGen;

public sealed partial class CodeGenerator
{
	/// <summary>
	/// Начало тела конструктора (spec §8, п.827–829): цепочка <c>: $(...)</c> либо пролог.
	/// Конструктор с цепочкой не выполняет инициализаторы полей/родителей — их уже выполнил
	/// вызванный конструктор, и повторный пролог нарушил бы правило «ровно один раз».
	/// Автоприсваивание собственных <c>$name</c>-параметров под это исключение не попадает.
	/// </summary>
	private void EmitConstructorEntry(BoundFunction function)
	{
		// Binder.Initializers.cs уже не даёт цепочке инициализаторов; assert ловит рассинхрон.
		Debug.Assert(
			function.Chain is null || function.Initializers.Count == 0,
			"цепочка конструкторов не должна нести инициализаторы");

		if (function.Symbol.Kind != FunctionKind.Constructor)
			return;

		if (function.Chain is { } chain)
		{
			EmitConstructorChain(function, chain);
			return;
		}

		EmitConstructorPrologue(function);
	}

	/// <summary>
	/// Вызов другого конструктора того же типа: <c>$</c> как получатель, аргументы, <c>Call</c>
	/// (тот же вид вызова, что у <c>NewObject</c> + ctor) и <c>Pop</c> результата-void.
	/// После вызова — автоприсваивание собственных <c>$name</c>-параметров: spec §8 п.829
	/// освобождает от повторного выполнения только инициализаторы полей и родителей, а
	/// <c>ctor(... $name)</c> — отдельный шаг п.827, поэтому он выполняется и у цепочки.
	/// </summary>
	private void EmitConstructorChain(BoundFunction function, BoundConstructorChain chain)
	{
		// Строка цепочки, а не объявления: паника в аргументе укажет на него.
		RecordLine(chain.Span);
		Emit(OpCode.LoadLocal, 0);
		foreach (var argument in chain.Arguments)
		{
			EmitExpression(argument);
			EmitCopyIfUnit(argument.Type);
		}

		if (!_functionIndices.TryGetValue(chain.Target, out var index))
		{
			Report(
				"KRN2201",
				$"Конструктор типа '{function.Symbol.Owner?.Name}' не имеет байткода (M3-D)",
				chain.Span);
			for (var argument = 0; argument < chain.Arguments.Count; argument++)
				Emit(OpCode.Pop);
			Emit(OpCode.Pop);
			EmitParameterFieldCopies(function);
			return;
		}

		Emit(OpCode.Call);
		EmitOperand(index);
		EmitOperand(chain.Arguments.Count + 1);
		// Конструктор всегда возвращает void (PushVoid); вызывающему результат не нужен.
		Emit(OpCode.Pop);

		EmitParameterFieldCopies(function);
	}

	/// <summary>
	/// Вызов конструктора прямого родителя в прологе корневого конструктора (slice A1):
	/// <c>$</c> как получатель, аргументы <c>[A(x)]</c>, <c>Call</c>, <c>Pop</c> результата-void.
	/// </summary>
	private void EmitBaseConstructorCall(BoundBaseConstructorCall call)
	{
		RecordLine(call.Span);
		Emit(OpCode.LoadLocal, 0);
		foreach (var argument in call.Arguments)
		{
			EmitExpression(argument);
			EmitCopyIfUnit(argument.Type);
		}

		if (!_functionIndices.TryGetValue(call.Target, out var index))
		{
			Report("KRN2201", $"Конструктор родителя '{call.Target.Owner?.Name}' не имеет байткода", call.Span);
			for (var argument = 0; argument < call.Arguments.Count; argument++)
				Emit(OpCode.Pop);
			Emit(OpCode.Pop);
			return;
		}

		Emit(OpCode.CallBase);
		EmitOperand(index);
		EmitOperand(call.Arguments.Count + 1);
		Emit(OpCode.Pop);
	}

	/// <summary>
	/// Пролог конструктора (spec §8, п.827): копии параметров primary-конструктора в их поля,
	/// затем инициализаторы полей и свойств в порядке объявления, затем копии
	/// <c>$</c>-параметров обычного конструктора, затем тело.
	/// </summary>
	private void EmitConstructorPrologue(BoundFunction function)
	{
		if (function.Symbol.Kind != FunctionKind.Constructor)
			return;

		// Пролог принадлежит объявлению конструктора, а не первому оператору тела.
		if (function.Symbol.Declaration is { } declaration)
			RecordLine(declaration.Span);

		// Родительская часть инициализируется до собственных инициализаторов наследника;
		// прямые родители вызываются в порядке объявления (slice A3a).
		foreach (var baseCall in function.BaseCalls)
			EmitBaseConstructorCall(baseCall);

		// Поля из параметров primary-конструктора входят в шаг «поля» (п.827):
		// инициализаторы должны видеть `$.x` уже присвоенным.
		var isPrimary = function.Symbol.IsPrimaryConstructor;
		if (isPrimary)
			EmitParameterFieldCopies(function);

		foreach (var initializer in function.Initializers)
			EmitFieldInitializer(initializer);

		// `ctor(... $name)`-автоприсваивание идёт после инициализаторов.
		if (!isPrimary)
			EmitParameterFieldCopies(function);
	}

	/// <summary>
	/// Копии <c>$</c>-параметров в их поля. Unit-значения копируются как при обычной передаче
	/// (spec §16, п.826), иначе поле делило бы экземпляр с вызывающим кодом.
	/// </summary>
	private void EmitParameterFieldCopies(BoundFunction function)
	{
		foreach (var parameter in function.Parameters)
		{
			if (parameter.Symbol.Field is not { } field)
				continue;
			if (!_fieldSlots.TryGetValue(field, out var slot))
			{
				Report("KRN2201", $"Поле '{field.Name}' не попало в TypeDef (M3-D)", parameter.Symbol.Span);
				continue;
			}

			Emit(OpCode.LoadLocal, 0);
			Emit(OpCode.LoadLocal, parameter.Slot);
			EmitCopyIfUnit(parameter.Symbol.Type);
			Emit(OpCode.StoreField, slot);
			Emit(OpCode.Pop);
		}
	}

	/// <summary>
	/// Инициализатор поля: <c>$</c>, значение выражения, StoreField в слот поля и Pop
	/// (StoreField оставляет Void). Unit-значения копируются как при обычном присваивании.
	/// </summary>
	private void EmitFieldInitializer(BoundFieldInitializer initializer)
	{
		if (!_fieldSlots.TryGetValue(initializer.Field, out var slot))
		{
			Report("KRN2201", $"Поле '{initializer.Field.Name}' не попало в TypeDef (M3-D)", initializer.Value.Span);
			return;
		}

		Emit(OpCode.LoadLocal, 0);
		// Строка инициализатора, а не объявления: паника в выражении укажет на него.
		RecordLine(initializer.Value.Span);
		EmitExpression(initializer.Value);
		EmitCopyIfUnit(initializer.Value.Type);
		Emit(OpCode.StoreField, slot);
		Emit(OpCode.Pop);
	}
}
