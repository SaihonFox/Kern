using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Xunit;

namespace Kern.Tests.Semantics.Declarations;

public sealed class OperatorAndParentSelectionTests
{
	[Fact]
	public void Collects_operator_member_with_symbol()
	{
		var vec = DeclarationTestSupport.Type(
			"type Vec(f64 x, f64 y) { op +(Vec other) -> Vec Vec(0, 0) }",
			"Vec");

		var @operator = Assert.Single(vec.Members.Operators);

		Assert.Equal(FunctionKind.Operator, @operator.Kind);
		Assert.Equal("+", @operator.OpSymbol);
		Assert.Equal("+", @operator.Name);
		Assert.True(@operator.IsOperator);
		Assert.Same(vec, @operator.Owner);
		Assert.Equal("Vec", Assert.Single(@operator.Parameters).Type.ToString());
	}

	[Fact]
	public void Collects_parent_method_and_field_selections()
	{
		var module = DeclarationTestSupport.Module(
			"type A { str name  fun hello(i32 x) -> str { \"a\" } } type B { str name } " +
			"type C()[A, B] { fun $[A].hello(i32 x) -> str  str $[A].name }");

		var c = module.FindType("C")!;
		var method = Assert.Single(c.Members.ParentSelections);
		var field = Assert.Single(c.Members.ParentFieldSelections);

		Assert.Equal("hello", method.Name);
		Assert.Equal(FunctionKind.ParentSelection, method.Kind);
		Assert.Equal("A", method.ParentSelector);
		Assert.Equal("str", method.ReturnType!.ToString());
		Assert.Equal("name", field.Name);
		Assert.Equal("A", field.ParentSelector);
		Assert.Empty(c.Members.GetFunctions("hello"));
		Assert.Null(c.Members.FindField("name"));
	}

	[Fact]
	public void Parent_method_selection_participates_in_member_name_conflicts()
	{
		var diagnostics = DeclarationTestSupport.Diagnostics(
			"type A { fun hello() -> str { \"a\" } } type C()[A] { str hello  fun $[A].hello() -> str }");

		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN1407");
	}

	[Fact]
	public void Extension_function_goes_to_registry_not_members()
	{
		var module = DeclarationTestSupport.Module(
			"type Money(i32 amount) { } fun Money.add(i32 other) -> Money Money(amount + other)");

		var group = module.FindExtensions("Money");
		var function = Assert.Single(group!.Functions);

		Assert.Equal("add", function.Name);
		Assert.Equal("Money", function.ReceiverType!.ToString());
		Assert.True(function.IsExtension);
		Assert.Equal(FunctionKind.Method, function.Kind);
		Assert.Empty(module.FindType("Money")!.Members.GetFunctions("add"));
	}

	[Fact]
	public void Extension_operator_uses_receiver_type_as_registry_key()
	{
		var module = DeclarationTestSupport.Module(
			"unit Vec(f64 x, f64 y) op i32.*(Vec v) -> Vec { Vec(1, 2) }");

		var group = module.FindExtensions(PrimitiveType.I32);
		var function = Assert.Single(group!.Functions);

		Assert.Equal("*", function.OpSymbol);
		Assert.Equal("i32", function.ReceiverType!.ToString());
		Assert.Equal(FunctionKind.Operator, function.Kind);
	}

	[Fact]
	public void Extension_property_is_registered_with_a_getter()
	{
		var module = DeclarationTestSupport.Module("i32 str.len2 5");

		var group = module.FindExtensions(PrimitiveType.Str);
		var property = Assert.Single(group!.Properties);

		Assert.Equal("len2", property.Name);
		Assert.Equal("i32", property.Type.ToString());
		Assert.True(property.IsExtension);
		Assert.NotNull(property.Getter);
		Assert.NotNull(property.ExpressionBody);
		Assert.Empty(group.Functions);
	}

	[Fact]
	public void Extension_function_declared_inside_a_type_is_registered_by_receiver()
	{
		var module = DeclarationTestSupport.Module("type Money(i32 amount) { } type Shopper { fun Money.add(i32 other) -> Money Money(amount + other) }");

		Assert.Empty(module.FindType("Shopper")!.Members.GetFunctions("add"));
		Assert.Single(module.FindExtensions("Money")!.Functions);
	}

	[Fact]
	public void Reports_duplicate_extension_function_signature()
	{
		var diagnostics = DeclarationTestSupport.Diagnostics(
			"type Money { } fun Money.add(i32 x) {} fun Money.add(i32 y) {}");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN1400", diagnostic.Code);
		Assert.Equal("Повторная сигнатура функции 'add' у расширения типа 'Money'", diagnostic.Message);
	}

	[Fact]
	public void Reports_extension_function_and_property_name_collision()
	{
		var diagnostics = DeclarationTestSupport.Diagnostics(
			"type Money { } fun Money.total(i32 x) {} i32 Money.total 5");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN1407", diagnostic.Code);
		Assert.Equal("Повторное имя члена 'total' у расширения типа 'Money'", diagnostic.Message);
	}
}
