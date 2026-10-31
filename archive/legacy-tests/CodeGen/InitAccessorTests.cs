using Kern.Compiler.CodeGen;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.CodeGen;

/// <summary>
/// M3-D3a: запись <c>init</c>-свойств только при создании объекта (spec §8, Q15b).
/// Разрешено: инициализаторы полей/свойств, пролог и тело конструктора того же типа
/// (<c>$</c>, включая вложенные блоки и ветвления); запрещено: методы, get/set/init-
/// accessor'ы, вложенные функции и запись в другой экземпляр.
/// </summary>
public sealed class InitAccessorTests
{
	[Fact]
	public void Init_value_written_in_constructor_body_is_visible_after_construction()
	{
		const string source =
			"""
			type Box {
				i32 value { get; init }
				ctor(i32 v) { $.value = v }
			}

			fun f() -> i32 {
				Box box = Box(42)
				box.value
			}
			""";

		Assert.Equal(KValue.I32(42), Run(source, "f"));
	}

	[Fact]
	public void Init_value_written_in_property_initializer_is_readable()
	{
		const string source =
			"""
			type Box {
				i32 value { get; init } = 5
			}

			fun f() -> i32 {
				Box box = Box()
				box.value
			}
			""";

		Assert.Equal(KValue.I32(5), Run(source, "f"));
	}

	[Fact]
	public void Init_value_written_in_chained_secondary_constructor_works()
	{
		const string source =
			"""
			type Box {
				i32 value { get; init }
				ctor(i32 start) { $.value = start }
				ctor() : $(1) { $.value = $.value + 1 }
			}

			fun f() -> i32 {
				Box box = Box()
				box.value
			}
			""";

		Assert.Equal(KValue.I32(2), Run(source, "f"));
	}

	[Fact]
	public void Init_unit_value_is_copied()
	{
		const string source =
			"""
			unit Point(i32 x)

			type Holder {
				Point p { get; init }
				ctor(Point v) { $.p = v }
			}

			fun f() -> i32 {
				Point original = Point(1)
				Holder holder = Holder(original)
				original.x = 2
				holder.p.x
			}
			""";

		// Поле хранит копию: изменение original после создания holder'а не влияет.
		Assert.Equal(KValue.I32(1), Run(source, "f"));
	}

	[Fact]
	public void Init_accessor_with_body_receives_the_assigned_value()
	{
		const string source =
			"""
			type Box {
				i32 _stored
				i32 value {
					get $._stored
					init $._stored = value
				}
				ctor(i32 v) { $.value = v }
			}

			fun f() -> i32 {
				Box box = Box(7)
				box.value
			}
			""";

		Assert.Equal(KValue.I32(7), Run(source, "f"));
	}

	[Fact]
	public void Two_writes_in_constructor_are_allowed_and_last_one_wins()
	{
		const string source =
			"""
			type Box {
				i32 value { get; init }
				ctor() {
					$.value = 1
					$.value = 2
				}
			}

			fun f() -> i32 {
				Box box = Box()
				box.value
			}
			""";

		Assert.Equal(KValue.I32(2), Run(source, "f"));
	}

	[Fact]
	public void Init_value_written_inside_nested_blocks_and_branches_is_allowed()
	{
		const string source =
			"""
			type Box {
				i32 value { get; init }
				ctor(i32 n) {
					if n > 0 {
						$.value = 1
					} else {
						$.value = 2
					}
				}
			}

			fun f(i32 n) -> i32 {
				Box box = Box(n)
				box.value
			}
			""";

		Assert.Equal(KValue.I32(1), Run(source, "f", KValue.I32(5)));
		Assert.Equal(KValue.I32(2), Run(source, "f", KValue.I32(-5)));
	}

	[Fact]
	public void Write_in_ordinary_method_reports_KRN2105()
	{
		var diagnostics = Diagnostics("""
			type Box {
				i32 value { get; init }
				fun touch() { $.value = 1 }
			}
			""");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN2105", diagnostic.Code);
		Assert.Contains("init", diagnostic.Message);
		Assert.NotEqual(default(TextSpan), diagnostic.Span);
	}

	[Fact]
	public void Write_in_getter_and_setter_reports_KRN2105()
	{
		var diagnostics = Diagnostics("""
			type Box {
				i32 value { get; init }
				i32 shadow {
					get { $.value = 1; 0 }
					set(v) { $.value = v }
				}
			}
			""");

		Assert.Equal(2, diagnostics.Count);
		Assert.All(diagnostics, diagnostic => Assert.Equal("KRN2105", diagnostic.Code));
	}

	[Fact]
	public void Write_to_another_instance_in_constructor_reports_KRN2105()
	{
		var diagnostics = Diagnostics("""
			type Box {
				i32 value { get; init }
				ctor(Box other) { other.value = 1 }
			}
			""");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN2105", diagnostic.Code);
		Assert.Contains("'$'", diagnostic.Message);
	}

	[Fact]
	public void Compound_write_to_init_outside_constructor_reports_KRN2105()
	{
		// Составное присваивание идёт в backing-поле и не должно обходить правила init.
		var diagnostics = Diagnostics("""
			type Box {
				i32 value { get; init }
				fun touch() { $.value += 1 }
			}
			""");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN2105", diagnostic.Code);
	}

	[Fact]
	public void Write_inside_lambda_declared_in_constructor_is_rejected()
	{
		// Лямбды пока не привязываются (KRN2109), поэтому вложенная функция не может
		// получить контекст создания: запись в lambda-теле не проходит как запись конструктора.
		var diagnostics = Diagnostics("""
			type Box {
				i32 value { get; init }
				ctor() {
					fun() -> i32 { $.value = 1; 0 }
				}
			}
			""");

		Assert.Contains(diagnostics, diagnostic => diagnostic.IsError);
	}

	[Fact]
	public void Write_in_init_accessor_body_reports_KRN2105()
	{
		// init-accessor — отдельная функция; контекст создания конструктора в неё не течёт.
		var diagnostics = Diagnostics("""
			type Box {
				i32 value { get; init }
				i32 other {
					get 0
					init $.value = value
				}
			}
			""");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN2105", diagnostic.Code);
	}

	[Fact]
	public void Init_value_written_in_constructor_of_immutable_unit_is_readable()
	{
		// spec §9: unit! неизменяем ПОСЛЕ создания; init в конструкторе — это создание.
		const string source =
			"""
			unit! Point {
				i32 x { get; init }
				ctor(i32 v) { $.x = v }
			}

			fun f() -> i32 {
				Point p = Point(3)
				p.x
			}
			""";

		Assert.Equal(KValue.I32(3), Run(source, "f"));
	}

	[Fact]
	public void Init_value_written_in_chained_constructor_of_immutable_unit_works()
	{
		const string source =
			"""
			unit! Point {
				i32 x { get; init }
				ctor(i32 v) { $.x = v }
				ctor() : $(1) { $.x = $.x + 1 }
			}

			fun f() -> i32 {
				Point p = Point()
				p.x
			}
			""";

		Assert.Equal(KValue.I32(2), Run(source, "f"));
	}

	[Fact]
	public void Write_to_init_in_method_of_immutable_unit_reports_KRN2105()
	{
		var diagnostics = Diagnostics("""
			unit! Point {
				i32 x { get; init }
				ctor(i32 v) { $.x = v }
				fun touch() { $.x = 1 }
			}
			""");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN2105", diagnostic.Code);
		Assert.Contains("init", diagnostic.Message);
	}

	[Fact]
	public void Write_to_set_in_immutable_unit_still_reports_KRN2105()
	{
		// set — путь изменения уже созданного объекта; для unit! он остаётся запрещён
		// (отдельная проверка в TryBindPropertyAssignment, не в init-пути).
		var diagnostics = Diagnostics("""
			unit! Point {
				i32 _x
				i32 x {
					get $._x
					set(v) { $._x = v }
				}
				fun touch() { $.x = 1 }
			}
			""");

		Assert.Contains(diagnostics, diagnostic =>
			diagnostic.Code == "KRN2105" &&
			diagnostic.Message.Contains("immutable unit!"));
	}

	[Fact]
	public void Required_field_assigned_in_constructor_binds_and_runs()
	{
		// spec §4: `!` у поля — обязательность инициализации при создании,
		// а не single-assignment после создания.
		Assert.Equal(
			KValue.Str("ann"),
			Run(
				"""
				type User {
					!str name
					ctor(str n) { $.name = n }
				}

				fun f() -> str {
					User u = User("ann")
					u.name
				}
				""",
				"f"));
	}

	[Fact]
	public void Immutable_unit_primary_constructor_fills_fields()
	{
		// Регрессия VM: запись полей unit! разрешена только в окне создания — пока
		// выполняется конструктор (spec §8); после возврата значение запечатано (spec §9).
		const string source =
			"""
			unit! Point(i32 x, i32 y)

			fun f() -> i32 {
				Point p = Point(3, 4)
				p.x * 10 + p.y
			}
			""";

		Assert.Equal(KValue.I32(34), Run(source, "f"));
	}

	private static KValue Run(string source, string function, params KValue[] arguments)
	{
		var result = Compile(source);
		Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.IsError);
		return new Interpreter(result.Module).Run(function, arguments);
	}

	private static CodegenResult Compile(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		var bound = new Binder(module, table).Bind();
		Assert.DoesNotContain(bound.Diagnostics, diagnostic => diagnostic.IsError);
		return new CodeGenerator().Generate(bound);
	}

	private static IReadOnlyList<Diagnostic> Diagnostics(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		return new Binder(module, table).Bind().Diagnostics;
	}
}
