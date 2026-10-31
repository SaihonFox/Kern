using Kern.Bytecode;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Tests.Bytecode;
using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Execution;

/// <summary>
/// Окно создания <c>unit!</c> (spec §8/§9): поля неизменяемого unit пишутся только пока
/// выполняется конструктор, после возврата значение запечатано. Тесты проверяют парность
/// окон на уровне <see cref="KObject"/> и их закрытие при панике/неуспешной задаче — окно
/// не должно пережить раскрутку, иначе объект навсегда останется записываемым.
/// </summary>
public sealed class ImmutableUnitConstructionTests
{
	// ---------- KObject: парность Begin/End ----------

	[Fact]
	public void Balanced_nested_windows_seal_after_the_last_end()
	{
		KObject point = NewImmutablePoint();

		point.BeginConstruction();
		point.BeginConstruction();
		point.SetField(0, KValue.I32(1));

		// Внутреннее окно закрылось, внешнее ещё открыто: запись разрешена.
		point.EndConstruction();
		point.SetField(0, KValue.I32(2));

		point.EndConstruction();
		KernPanic error = Assert.Throws<KernPanic>(() => point.SetField(0, KValue.I32(3)));
		Assert.Contains("нельзя изменить поле", error.Message);
	}

	[Fact]
	public void Two_begins_and_two_ends_seal_the_object()
	{
		KObject point = NewImmutablePoint();
		point.BeginConstruction();
		point.BeginConstruction();
		point.EndConstruction();
		point.EndConstruction();

		Assert.Throws<KernPanic>(() => point.SetField(0, KValue.I32(1)));
	}

	[Fact]
	public void Closed_window_can_be_opened_again_by_a_new_constructor_call()
	{
		KObject point = NewImmutablePoint();
		point.BeginConstruction();
		point.EndConstruction();
		Assert.Throws<KernPanic>(() => point.SetField(0, KValue.I32(1)));

		// Новое окно (например, повторный вызов конструктора) снова разрешает запись.
		point.BeginConstruction();
		point.SetField(0, KValue.I32(2));
		point.EndConstruction();
		Assert.Equal(KValue.I32(2), point.GetField(0));
		Assert.Throws<KernPanic>(() => point.SetField(0, KValue.I32(3)));
	}

	[Fact]
	public void End_without_begin_fails_loudly()
	{
		KObject point = NewImmutablePoint();

		KernPanic error = Assert.Throws<KernPanic>(() => point.EndConstruction());

		Assert.Contains("закрытие окна создания без открытия", error.Message);
	}

	[Fact]
	public void Second_end_after_a_balanced_pair_fails_loudly()
	{
		KObject point = NewImmutablePoint();
		point.BeginConstruction();
		point.EndConstruction();

		// Непарное закрытие не должно тихо «съедаться» и открывать постоянную запись.
		KernPanic error = Assert.Throws<KernPanic>(() => point.EndConstruction());

		Assert.Contains("закрытие окна создания без открытия", error.Message);
		Assert.Throws<KernPanic>(() => point.SetField(0, KValue.I32(1)));
	}

	// ---------- source: значение, полученное из VM ----------

	[Fact]
	public void Constructed_immutable_unit_is_sealed_for_escaped_reference()
	{
		const string source =
			"""
			unit! Point(i32 x)

			fun make() -> Point { Point(3) }
			""";

		KObject point = Assert.IsType<KObject>(RunSource(source, "make").Ref);
		Assert.Equal(KValue.I32(3), point.GetField(0));

		KernPanic error = Assert.Throws<KernPanic>(() => point.SetField(0, KValue.I32(9)));

		Assert.Contains("нельзя изменить поле", error.Message);
		Assert.Contains("unit-типа", error.Message);
	}

	[Fact]
	public void Copy_of_constructed_immutable_unit_is_sealed()
	{
		// unit! — значение: присваивание делает копию (CopyUnit), у копии тот же
		// неизменяемый тип, поэтому окно создания ей не передаётся.
		const string source =
			"""
			unit! Point(i32 x)

			fun make() -> Point {
				Point original = Point(3)
				Point copy = original
				copy
			}
			""";

		KObject copy = Assert.IsType<KObject>(RunSource(source, "make").Ref);
		Assert.Equal(KValue.I32(3), copy.GetField(0));

		Assert.Throws<KernPanic>(() => copy.SetField(0, KValue.I32(9)));
	}

	[Fact]
	public void Chained_secondary_constructor_works_and_is_sealed_afterwards()
	{
		const string source =
			"""
			unit! Point {
				i32 x { get; init }
				ctor(i32 v) { $.x = v }
				ctor() : $(1) { $.x = $.x + 1 }
			}

			fun make() -> Point { Point() }
			""";

		KObject point = Assert.IsType<KObject>(RunSource(source, "make").Ref);
		Assert.Equal(KValue.I32(2), point.GetField(0));

		Assert.Throws<KernPanic>(() => point.SetField(0, KValue.I32(9)));
	}

	// ---------- байткод: паника в конструкторе ----------

	[Fact]
	public void Panic_in_constructor_closes_construction_window()
	{
		(TypeDef definition, KbcModule module) = PanickingConstructorModule(chain: false);
		Interpreter interpreter = new(module);
		KObject point = NewPoint(interpreter, definition);

		KernPanic panic = Assert.Throws<KernPanic>(
			() => interpreter.Run("main", [KValue.Obj(point), KValue.I32(5)]));

		Assert.Contains("деление на ноль", panic.Message);
		// Запись до паники прошла: окно было открыто.
		Assert.Equal(KValue.I32(5), point.GetField(0));
		// После раскрутки окно закрыто.
		KernPanic write = Assert.Throws<KernPanic>(() => point.SetField(0, KValue.I32(9)));
		Assert.Contains("нельзя изменить поле", write.Message);
		Assert.Equal(KValue.I32(5), point.GetField(0));
	}

	[Fact]
	public void Panic_in_chained_constructor_closes_all_nested_windows()
	{
		(TypeDef definition, KbcModule module) = PanickingConstructorModule(chain: true);
		Interpreter interpreter = new(module);
		KObject point = NewPoint(interpreter, definition);

		KernPanic panic = Assert.Throws<KernPanic>(
			() => interpreter.Run("main", [KValue.Obj(point), KValue.I32(5)]));

		Assert.Contains("деление на ноль", panic.Message);
		// primary, secondary, main — все кадры попали в трассировку.
		Assert.Equal(3, panic.Frames.Count);
		Assert.Equal(KValue.I32(5), point.GetField(0));
		// Непарное окно цепочки осталось бы глубины 1 и запись бы прошла.
		Assert.Throws<KernPanic>(() => point.SetField(0, KValue.I32(9)));
	}

	// ---------- байткод: задача, ожидаемая внутри конструктора ----------

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void Awaiting_constructor_window_closes_when_task_fails_or_is_canceled(bool fail)
	{
		TypeDef definition = ImmutablePointDefinition();
		FunctionDef ctor = PointConstructor(
			new CodeBuilder()
				.Load(0)
				.Push(5)
				.Op(OpCode.StoreField, 0)
				.Op(OpCode.Pop)
				.Load(1)
				.Op(OpCode.Await)
				.Op(OpCode.Pop)
				.Op(OpCode.PushVoid)
				.Op(OpCode.Ret)
				.Build(),
			parameterCount: 2);
		FunctionDef main = TestVm.Function(
			"main",
			new CodeBuilder()
				.Load(0)
				.Load(1)
				.Op(OpCode.Call, 0, 2)
				.Op(OpCode.Pop)
				.Op(OpCode.PushVoid)
				.Op(OpCode.Ret)
				.Build(),
			paramCount: 2);
		KbcModule module = TestVm.Module(ctor, main);
		module.TypePool.Add(definition);

		// Припаркованное состояние живёт в потоке-владельце планировщика, поэтому
		// интерпретатор создаётся в рабочем потоке, а провал задачи ставится в очередь
		// планировщика: продолжение выполнится уже после парковки на Await.
		KTask task = new();
		Interpreter? interpreter = null;
		KObject? created = null;
		Exception? failure = null;
		using ManualResetEventSlim ready = new(false);
		Thread worker = new(() =>
		{
			try
			{
				interpreter = new Interpreter(module);
				created = NewPoint(interpreter, definition);
				ready.Set();
				interpreter.Run("main", [KValue.Obj(created), KValue.Obj(task)]);
			}
			catch (Exception error)
			{
				failure = error;
			}
		})
		{
			IsBackground = true,
		};
		worker.Start();

		Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "поток VM не создал интерпретатор");
		interpreter!.Scheduler.Post(() =>
		{
			if (fail)
				task.TrySetError(new KernPanic("тестовая ошибка"));
			else
				task.TrySetCanceled();
		});
		Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "поток VM не завершился");

		KernPanic panic = Assert.IsType<KernPanic>(failure);
		Assert.Contains(fail ? "тестовая ошибка" : "задача отменена", panic.Message);
		KObject point = Assert.IsType<KObject>(created);
		// Поле записано внутри конструктора: окно было открыто до парковки.
		Assert.Equal(KValue.I32(5), point.GetField(0));
		// Провал/отмена задачи раскрутили состояние — окно обязано закрыться.
		Assert.Throws<KernPanic>(() => point.SetField(0, KValue.I32(9)));
	}

	// ---------- поддержка ----------

	private static KObject NewImmutablePoint() => NewPointFor(ImmutablePointDefinition());

	private static KObject NewPointFor(TypeDef definition)
	{
		KbcModule module = new() { Name = "test" };
		module.TypePool.Add(definition);
		return new KObject(new TypeFactory(module).Named(definition), [KValue.None]);
	}

	private static KObject NewPoint(Interpreter interpreter, TypeDef definition) =>
		new(interpreter.Types.Named(definition), [KValue.None]);

	private static TypeDef ImmutablePointDefinition() => new()
	{
		Name = "Point",
		FullName = "Test.Point",
		Kind = TypeDefKind.ImmutableUnit,
		Fields = [new FieldDef { Name = "x", Slot = 0 }],
	};

	private static FunctionDef PointConstructor(int[] code, int parameterCount)
	{
		FunctionDef ctor = TestVm.Function("Point.ctor", code, paramCount: parameterCount);
		ctor.OwnerType = 0;
		ctor.Flags = MemberFlags.Ctor;
		return ctor;
	}

	/// <summary>
	/// Конструктор пишет поле (окно открыто), затем падает делением на ноль.
	/// <paramref name="chain"/> добавляет вторичный конструктор, вызывающий первичный:
	/// паника тогда раскручивает два вложенных окна одного объекта.
	/// </summary>
	private static (TypeDef Definition, KbcModule Module) PanickingConstructorModule(bool chain)
	{
		TypeDef definition = ImmutablePointDefinition();
		FunctionDef primary = PointConstructor(
			new CodeBuilder()
				.Load(0)
				.Load(1)
				.Op(OpCode.StoreField, 0)
				.Op(OpCode.Pop)
				.Push(1)
				.Push(0)
				.Op(OpCode.Div)
				.Op(OpCode.Pop)
				.Op(OpCode.PushVoid)
				.Op(OpCode.Ret)
				.Build(),
			parameterCount: 2);
		FunctionDef secondary = TestVm.Function(
			"Point.ctor_chained",
			new CodeBuilder()
				.Load(0)
				.Load(1)
				.Op(OpCode.Call, 0, 2)
				.Op(OpCode.Pop)
				.Op(OpCode.PushVoid)
				.Op(OpCode.Ret)
				.Build(),
			paramCount: 2);
		secondary.OwnerType = 0;
		secondary.Flags = MemberFlags.Ctor;
		FunctionDef main = TestVm.Function(
			"main",
			new CodeBuilder()
				.Load(0)
				.Load(1)
				.Op(OpCode.Call, chain ? 1 : 0, 2)
				.Op(OpCode.Pop)
				.Op(OpCode.PushVoid)
				.Op(OpCode.Ret)
				.Build(),
			paramCount: 2);

		KbcModule module = chain
			? TestVm.Module(primary, secondary, main)
			: TestVm.Module(primary, main);
		module.TypePool.Add(definition);
		return (definition, module);
	}

	private static KValue RunSource(string source, string function)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		var bound = new Binder(module, table).Bind();
		Assert.DoesNotContain(bound.Diagnostics, diagnostic => diagnostic.IsError);
		var result = new CodeGenerator().Generate(bound);
		Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.IsError);
		return new Interpreter(result.Module).Run(function, []);
	}
}
