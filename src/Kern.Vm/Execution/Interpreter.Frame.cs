using Kern.Bytecode;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>Кадр вызова VM (часть partial-класса Interpreter).</summary>
public sealed partial class Interpreter
{
	internal struct Frame
	{
		public FunctionDef Function;
		public KValue[] Locals;
		public KValue[]? Captures;
		public int Pc;
		public int StackBase;
		/// <summary>
		/// Объект неизменяемого unit, окно создания которого открыл <c>Call</c>, сохранивший
		/// этот кадр (конструктор ещё не вернулся). Кадр помнит собственный вызов, поэтому
		/// <c>Ret</c> вызываемого закрывает окно, а раскрутка по панике снимает его через
		/// <see cref="CloseConstructionWindows"/>. <c>null</c> — вызов окна не открывал.
		/// </summary>
		public KObject? Construction;

		/// <summary>
		/// Аргументы вызова, запущенного из этого кадра, если функция помечена
		/// <c>#[memo]</c>: <c>Ret</c> вызываемого кладёт результат в кэш.
		/// </summary>
		public KValue[]? MemoArguments;

		/// <summary>Метка старта профилировщика для вызова, сохранившего этот кадр; 0 — профиль выключен.</summary>
		public long ProfileStart;

		public Frame(
			FunctionDef function,
			KValue[] locals,
			KValue[]? captures,
			int pc,
			int stackBase,
			KObject? construction,
			KValue[]? memoArguments = null,
			long profileStart = 0)
		{
			Function = function;
			Locals = locals;
			Captures = captures;
			Pc = pc;
			StackBase = stackBase;
			Construction = construction;
			MemoArguments = memoArguments;
			ProfileStart = profileStart;
		}
	}
}
