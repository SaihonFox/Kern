using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Bytecode;
using Kern.Runtime.Core;
using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Threading;

/// <summary>
/// Запущенный поток с результатом <c>T</c> (spec round 24). Все потоки видят одни и те же
/// объекты и статику; каждый рабочий поток получает собственный форк интерпретатора.
/// </summary>
[KernType("worker", GenericArity = 1)]
public sealed class KernWorker
{
	private readonly Thread _thread;
	private KValue _result = KValue.None;
	private string? _error;
	private volatile bool _done;

	private KernWorker(Interpreter parent, KValue function, KValue[] arguments)
	{
		_thread = new Thread(() => Body(parent, function, arguments), VmExec.StackSize)
		{
			IsBackground = true,
		};
	}

	/// <summary>Завершён ли поток.</summary>
	public bool IsDone => _done;

	internal static KernWorker Start(Interpreter parent, KValue function, KValue[] arguments)
	{
		var worker = new KernWorker(parent, function, arguments);
		worker._thread.Start();
		return worker;
	}

	/// <summary>Блокирующее ожидание: <c>Ok(результат)</c> или <c>Err(сообщение паники)</c>.</summary>
	internal KValue JoinResult(Interpreter interpreter, KType resultType)
	{
		_thread.Join();
		if (_error is null)
			return KernResults.Ok(interpreter, resultType, _result);
		return KernResults.Err(
			interpreter,
			interpreter.Types.Prim(PrimKind.Str),
			KValue.Str(_error));
	}

	/// <summary>Асинхронное ожидание (по завершении потока), результат собирается в его VM.</summary>
	internal async Task<KValue> WaitResult(Interpreter interpreter, KType resultType)
	{
		await Task.Run(() => _thread.Join()).ConfigureAwait(false);
		return JoinResult(interpreter, resultType);
	}

	private void Body(Interpreter parent, KValue function, KValue[] arguments)
	{
		try
		{
			// Форк создаётся в рабочем потоке: его планировщик принадлежит этому потоку.
			Interpreter worker = parent.Fork();
			_result = VmExec.Run(worker, function, arguments);
		}
		catch (Exception error)
		{
			_error = VmExec.Describe(error);
		}
		finally
		{
			_done = true;
		}
	}
}

/// <summary>Фабрика и члены <c>worker&lt;T&gt;</c> (spec round 24).</summary>
[KernType("worker", GenericArity = 1)]
public static class WorkerApi
{
	[KernName("is_done")]
	[KernProperty]
	public static bool IsDone(KNativeBox self) => Require(self).IsDone;

	/// <summary>Блокирующее ожидание: поток останавливает цикл событий вызывающего.</summary>
	[KernMember]
	[KernReturn("result<T, str>")]
	public static KValue Join(Interpreter interpreter, KNativeBox self, [KernTypeArgs] KType[] typeArguments) =>
		Require(self).JoinResult(interpreter, ResultType(interpreter, typeArguments));

	/// <summary>Асинхронное ожидание: <c>await w.wait()</c>.</summary>
	[KernMember]
	[KernReturn("task<result<T, str>>")]
	public static Task<KValue> Wait(Interpreter interpreter, KNativeBox self, [KernTypeArgs] KType[] typeArguments) =>
		Require(self).WaitResult(interpreter, ResultType(interpreter, typeArguments));

	private static KType ResultType(Interpreter interpreter, KType[] typeArguments) =>
		typeArguments.Length > 0 ? typeArguments[0] : interpreter.Types.Prim(PrimKind.Object);

	private static KernWorker Require(KNativeBox self) => self.Value switch
	{
		KernWorker worker => worker,
		_ => throw new KernPanic($"worker: ожидался поток, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>
/// <c>thread.*</c> (spec round 24): запуск потоков с общей памятью, сон, идентификатор,
/// выполнение в общем пуле. Потоки видят те же объекты, статику и замыкания.
/// </summary>
[KernType("thread")]
public static class ThreadApi
{
	/// <summary><c>thread.spawn(f)</c>: запустить функцию без аргументов в новом потоке.</summary>
	[KernStatic]
	[KernName("spawn")]
	[KernReturn("worker<T>")]
	public static KValue Spawn<T>(Interpreter interpreter, KFunc<T> function) =>
		KValue.Obj(new KNativeBox(KernWorker.Start(interpreter, function.Value, [])));

	/// <summary><c>thread.spawn_with(f, arg)</c>: запустить функцию с одним аргументом.</summary>
	[KernStatic]
	[KernName("spawn_with")]
	[KernReturn("worker<T>")]
	public static KValue SpawnWith<TArgument, T>(Interpreter interpreter, KFunc<TArgument, T> function, TArgument argument) =>
		KValue.Obj(new KNativeBox(KernWorker.Start(
			interpreter, function.Value, [KMarshalArgument(argument)])));

	/// <summary><c>thread.spawn_with(f, a, b)</c>: запустить функцию с двумя аргументами.</summary>
	[KernStatic]
	[KernName("spawn_with")]
	[KernReturn("worker<T>")]
	public static KValue SpawnWith<TFirst, TSecond, T>(
		Interpreter interpreter,
		KFunc<TFirst, TSecond, T> function,
		TFirst first,
		TSecond second) =>
		KValue.Obj(new KNativeBox(KernWorker.Start(
			interpreter, function.Value, [KMarshalArgument(first), KMarshalArgument(second)])));

	/// <summary><c>thread.run(f)</c>: выполнить в общем пуле, не блокируя цикл событий.</summary>
	[KernStatic]
	[KernName("run")]
	[KernReturn("task<T>")]
	public static KValue Run<T>(Interpreter interpreter, KFunc<T> function) =>
		RunInPool(interpreter, function.Value, []);

	/// <summary><c>thread.run_with(f, arg)</c>.</summary>
	[KernStatic]
	[KernName("run_with")]
	[KernReturn("task<T>")]
	public static KValue RunWith<TArgument, T>(
		Interpreter interpreter,
		KFunc<TArgument, T> function,
		TArgument argument) =>
		RunInPool(interpreter, function.Value, [KMarshalArgument(argument)]);

	/// <summary><c>thread.sleep(ms)</c>: блокирующая пауза текущего потока.</summary>
	[KernStatic]
	public static void Sleep(int milliseconds) => Thread.Sleep(System.Math.Max(0, milliseconds));

	/// <summary><c>thread.yield()</c>: уступить процессор другому потоку.</summary>
	[KernStatic]
	public static void Yield() => Thread.Yield();

	/// <summary><c>thread.cpu_count()</c>: число логических ядер.</summary>
	[KernStatic]
	[KernName("cpu_count")]
	public static int CpuCount() => Environment.ProcessorCount;

	/// <summary><c>thread.current_id()</c>: идентификатор текущего управляемого потока.</summary>
	[KernStatic]
	[KernName("current_id")]
	public static int CurrentId() => Environment.CurrentManagedThreadId;

	internal static KValue RunInPool(Interpreter caller, KValue function, KValue[] arguments)
	{
		var task = new KTask();
		Task.Run(() =>
		{
			try
			{
				Interpreter worker = caller.Fork();
				KValue result = VmExec.Run(worker, function, arguments);
				caller.Scheduler.EndExternal(() => task.Complete(result));
			}
			catch (Exception error)
			{
				KernPanic panic = error as KernPanic ?? new KernPanic(VmExec.Describe(error), error);
				caller.Scheduler.EndExternal(() => task.Fail(panic));
			}
		});
		return KValue.Obj(task);
	}

	private static KValue KMarshalArgument(object? value) => Kern.Binding.Marshalling.KMarshal.FromObject(value);
}
