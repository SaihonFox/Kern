using Kern.Bytecode;
using Kern.Vm.Async;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>Запуск программы: статические инициализаторы, `main`, ожидание async-результата.</summary>
public sealed partial class Interpreter
{
	/// <summary>Выполняет точку входа модуля и возвращает код завершения процесса.</summary>
	public int RunMain(string[] args, TextWriter? errors = null)
	{
		errors ??= Console.Error;
		try
		{
			if (Module.EntryPoint < 0) throw new KernPanic("в модуле нет точки входа `main`");
			Initialize();
			KValue[] callArgs = Module.EntryTakesArgs
				? [KValue.Obj(new KListObject(TypeFactory.Prim(PrimKind.Str), args.Select(KValue.Str).ToList()))]
				: [];
			var r = CallFunctionIndex(Module.EntryPoint, callArgs);
			if (r.Ref is KTask t)
			{
				if (!t.IsCompleted && !Scheduler.RunUntil(() => t.IsCompleted))
					throw new KernPanic("взаимная блокировка: main никогда не завершится");
				r = t.Unwrap();
			}
			Scheduler.RunUntil(() => Scheduler.PendingExternal == 0 && Scheduler.RunPending() == 0);
			return r.IsInteger ? (int)r.Bits : 0;
		}
		catch (KernExit e) { return e.Code; }
		catch (KernPanic p) { errors.WriteLine(p.Format()); return 101; }
	}
}
