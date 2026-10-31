using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Xunit;

namespace Kern.Tests.Semantics.Binding;

/// <summary>M3-D: члены unit/type — `$`, методы, поля и конструкторы.</summary>
public sealed class BinderMemberTests
{
	private const string PointWithGetter = """
		unit Point(i32 x) {
			fun get() -> i32 { $x }
		}
		""";

	[Fact]
	public void Synthetic_dollar_occupies_slot_zero_before_explicit_parameters()
	{
		var module = Bind(PointWithGetter + """

			fun f() -> i32 {
				Point p = Point(7)
				p.get()
			}
			""");

		// У метода без объявленных параметров есть только синтетический `$`.
		var get = module.FindFunction("get")!;
		var self = Assert.Single(get.Parameters);
		Assert.Equal("$", self.Symbol.Name);
		Assert.Equal(0, self.Slot);
		Assert.Equal("Point", Assert.IsType<UnitType>(self.Symbol.Type).Symbol.Name);

		// Явный параметр primary-конструктора `x` идёт сразу после `$`.
		var constructor = module.Functions.Single(function =>
			function.Symbol.Kind == FunctionKind.Constructor &&
			function.Symbol.Owner?.Name == "Point");
		Assert.Equal(2, constructor.Parameters.Count);
		Assert.Equal("$", constructor.Parameters[0].Symbol.Name);
		Assert.Equal(0, constructor.Parameters[0].Slot);
		Assert.Equal("x", constructor.Parameters[1].Symbol.Name);
		Assert.Equal(1, constructor.Parameters[1].Slot);
	}

	[Fact]
	public void Instance_method_call_carries_receiver_and_owner()
	{
		var module = Bind(PointWithGetter + """

			fun f() -> i32 {
				Point p = Point(7)
				p.get()
			}
			""");
		var function = module.FindFunction("f")!;
		var statement = Assert.IsType<BoundExpressionStatement>(function.Body.Statements[1]);
		var call = Assert.IsType<BoundCallExpression>(statement.Expression);

		Assert.NotNull(call.Receiver);
		Assert.NotNull(call.Function.Owner);
		Assert.Equal("Point", call.Function.Owner!.Name);
		Assert.Equal(PrimitiveType.I32, call.Type);
	}

	[Fact]
	public void Field_assignment_binds_as_bound_field_assignment()
	{
		var module = Bind("""
			unit Point(i32 x)

			fun f {
				Point p = Point(1)
				p.x = 5
			}
			""");
		var function = module.FindFunction("f")!;
		var assignment = Assert.IsType<BoundFieldAssignment>(function.Body.Statements[1]);

		Assert.Equal("x", assignment.Field.Name);
		Assert.NotNull(assignment.Receiver);
		Assert.Equal(PrimitiveType.I32, assignment.Type);
	}

	[Fact]
	public void Constructor_call_binds_as_object_creation_with_constructor()
	{
		var module = Bind("""
			unit Point(i32 x)

			fun f {
				Point p = Point(7)
			}
			""");
		var function = module.FindFunction("f")!;
		var declaration = Assert.IsType<BoundLocalDeclaration>(function.Body.Statements[0]);
		var creation = Assert.IsType<BoundObjectCreationExpression>(declaration.Initializer);

		Assert.NotNull(creation.Constructor);
		Assert.Equal("Point", creation.TypeSymbol.Name);
		Assert.Single(creation.Arguments);
	}

	[Fact]
	public void Field_assignment_on_immutable_unit_reports_KRN2105()
	{
		var diagnostics = Diagnostics("""
			unit! Point(i32 x)

			fun f {
				Point p = Point(7)
				p.x = 5
			}
			""");

		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2105");
	}

	[Fact]
	public void Instance_initializers_are_bound_in_declaration_order()
	{
		var module = Bind("""
			type Counter {
				i32 first = 1
				i32 val { get; set } = 2
			}
			""");
		var constructor = Assert.Single(module.Functions, function =>
			function.Symbol.Kind == FunctionKind.Constructor &&
			function.Symbol.Owner?.Name == "Counter");

		Assert.Equal(
			["first", "_val"],
			constructor.Initializers.Select(initializer => initializer.Field.Name));
		Assert.Equal(
			["1", "2"],
			constructor.Initializers.Select(initializer =>
				Assert.IsType<BoundLiteralExpression>(initializer.Value).Numeric!.IntegerValue.ToString()));
	}

	[Fact]
	public void Static_field_initializer_binds_into_the_type_static_initializer()
	{
		var module = Bind("""
			type Counter {
				#i32 seed = 1
			}
			""");

		var initializer = Assert.Single(module.Functions, function =>
			function.Symbol.Name == "<static-init>" &&
			function.Symbol.Owner?.Name == "Counter");
		var assignment = Assert.IsType<BoundFieldAssignment>(Assert.Single(initializer.Body.Statements));

		Assert.Null(assignment.Receiver);
		Assert.Equal("seed", assignment.Field.Name);
		Assert.True(assignment.Field.IsStatic);
	}

	private static BoundModule Bind(string source)
	{
		var (module, diagnostics) = BindWithDiagnostics(source);
		Assert.Empty(diagnostics);
		return module;
	}

	private static IReadOnlyList<Diagnostic> Diagnostics(string source) =>
		BindWithDiagnostics(source).Diagnostics;

	private static (BoundModule Module, IReadOnlyList<Diagnostic> Diagnostics) BindWithDiagnostics(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		var bound = new Binder(module, table).Bind();
		return (bound, bound.Diagnostics);
	}
}
