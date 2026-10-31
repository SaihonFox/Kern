using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Execution;

/// <summary>
/// Статическая инициализация модуля (Q15b): <see cref="KbcModule.StaticInit"/> выполняется
/// ровно один раз, до первой пользовательской функции, и общий для форков; ошибка
/// инициализатора «залипает» и не повторяется (spec §12).
/// </summary>
public sealed class StaticInitTests
{
	private const int InitIndex = 0;
	private const int ReadIndex = 1;

	private static KbcModule StaticInitModule(bool panics = false)
	{
		KbcModule module = new() { Name = "static_init" };
		module.FieldPool.Add(new FieldDef { Name = "counter", Type = TypeSig.I32, Slot = 0 });
		module.FunctionPool.Add(new FunctionDef
		{
			Name = "<static-init>",
			Flags = MemberFlags.Static,
			MaxStack = 8,
			Code = CounterIncrementCode(panics),
		});
		module.FunctionPool.Add(TestVm.Function("read",
		[
			(int)OpCode.LoadStatic, 0,
			(int)OpCode.Ret,
		]));
		module.StaticInit = InitIndex;
		return module;
	}

	/// <summary>
	/// <c>counter = (counter == none ? 0 : counter) + 1</c>: слот статики стартует как
	/// <c>none</c>, поэтому первый запуск сначала кладёт 0, а повторный инкрементирует.
	/// Хвост — либо <c>Ret</c>, либо паника <c>MatchFail</c>.
	/// </summary>
	private static int[] CounterIncrementCode(bool panics)
	{
		int[] body =
		[
			(int)OpCode.LoadStatic, 0,
			(int)OpCode.PushNone,
			(int)OpCode.Eq,
			(int)OpCode.JmpIfNot, 11,
			(int)OpCode.PushI32, 0,
			(int)OpCode.StoreStatic, 0,
			(int)OpCode.Pop,
			(int)OpCode.LoadStatic, 0,
			(int)OpCode.PushI32, 1,
			(int)OpCode.Add,
			(int)OpCode.StoreStatic, 0,
		];
		return panics ? [.. body, (int)OpCode.MatchFail] : [.. body, (int)OpCode.Ret];
	}

	[Fact]
	public void Run_sees_value_written_by_static_init()
	{
		Interpreter interpreter = new(StaticInitModule());

		KValue value = interpreter.Run("read", []);

		Assert.Equal(KValue.I32(1), value);
	}

	[Fact]
	public void Static_init_runs_once_for_repeated_runs()
	{
		Interpreter interpreter = new(StaticInitModule());

		Assert.Equal(KValue.I32(1), interpreter.Run("read", []));
		Assert.Equal(KValue.I32(1), interpreter.Run("read", []));
		Assert.Equal(KValue.I32(1), interpreter.Run(ReadIndex, []));
	}

	[Fact]
	public void RunEntry_runs_static_init_before_entry_point()
	{
		KbcModule module = StaticInitModule();
		module.EntryPoint = ReadIndex;

		KValue value = new Interpreter(module).RunEntry();

		Assert.Equal(KValue.I32(1), value);
	}

	[Fact]
	public void Fork_sees_static_init_done_by_the_owner()
	{
		Interpreter owner = new(StaticInitModule());
		Assert.Equal(KValue.I32(1), owner.Run("read", []));

		Interpreter worker = owner.Fork();

		Assert.Equal(KValue.I32(1), worker.Run("read", []));
		Assert.Equal(KValue.I32(1), owner.Run("read", []));
	}

	[Fact]
	public void Fork_runs_the_shared_static_init_once_when_it_runs_first()
	{
		Interpreter owner = new(StaticInitModule());
		Interpreter worker = owner.Fork();

		Assert.Equal(KValue.I32(1), worker.Run("read", []));
		Assert.Equal(KValue.I32(1), owner.Run("read", []));
	}

	[Fact]
	public void Module_without_static_init_runs_as_before()
	{
		FunctionDef main = TestVm.Function("main", [(int)OpCode.PushI32, 42, (int)OpCode.Ret]);
		KbcModule module = TestVm.Module(main);

		Assert.Equal(-1, module.StaticInit);
		Assert.Equal(KValue.I32(42), new Interpreter(module).Run("main", []));
	}

	[Fact]
	public void RunValue_does_not_trigger_static_init()
	{
		KbcModule module = StaticInitModule();
		Interpreter interpreter = new(module);

		KValue before = interpreter.RunValue(KValue.Obj(module.FunctionPool[ReadIndex]), []);

		Assert.Equal(KValue.None, before);
		Assert.Equal(KValue.I32(1), interpreter.Run("read", []));
	}

	[Fact]
	public void Panicking_static_init_is_sticky_and_not_repeated()
	{
		KbcModule module = StaticInitModule(panics: true);
		Interpreter interpreter = new(module);

		KernPanic first = Assert.Throws<KernPanic>(() => interpreter.Run("read", []));
		KernPanic second = Assert.Throws<KernPanic>(() => interpreter.Run("read", []));

		Assert.Equal(first.Message, second.Message);
		// RunValue не запускает статическую инициализацию, поэтому слот читается напрямую:
		// повторного запуска не было, счётчик остался 1.
		KValue counter = interpreter.RunValue(KValue.Obj(module.FunctionPool[ReadIndex]), []);
		Assert.Equal(KValue.I32(1), counter);
	}
}
