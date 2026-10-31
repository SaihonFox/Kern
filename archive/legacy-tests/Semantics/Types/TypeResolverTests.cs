using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using SymbolKind = Kern.Compiler.Semantics.Types.SymbolKind;
using Xunit;

namespace Kern.Tests.Semantics.Types;

public sealed class TypeResolverTests
{
	[Fact]
	public void Resolves_nested_builtin_generics()
	{
		var type = TypeTestSupport.Resolve("", "list<dict<str, i32>>");

		Assert.Equal("list<dict<str,i32>>", type.ToString());
		var list = Assert.IsType<BuiltinGenericType>(type);
		Assert.Equal(BuiltinGenericKind.List, list.BuiltinKind);

		var dict = Assert.IsType<BuiltinGenericType>(Assert.Single(list.Arguments));
		Assert.Equal(BuiltinGenericKind.Dict, dict.BuiltinKind);
		Assert.Equal(["str", "i32"], dict.Arguments.Select(argument => argument.ToString()));
	}

	[Fact]
	public void Resolves_collection_as_a_builtin_generic_root()
	{
		var type = TypeTestSupport.Resolve("", "collection<char>");
		var collection = Assert.IsType<BuiltinGenericType>(type);

		Assert.Equal(BuiltinGenericKind.Collection, collection.BuiltinKind);
		Assert.Equal(TypeKind.BuiltinGeneric, collection.Kind);
		Assert.Equal("collection<char>", type.ToString());
	}

	[Fact]
	public void Map_alias_resolves_to_the_same_type_as_dict()
	{
		var resolver = TypeTestSupport.ResolverFor("");
		var dict = resolver.Resolve(TypeTestSupport.ParseType("dict<str,i32>"));
		var map = resolver.Resolve(TypeTestSupport.ParseType("map<str,i32>"));

		Assert.Empty(resolver.Diagnostics);
		Assert.Equal(dict, map);
		Assert.Equal("dict<str,i32>", map.ToString());
	}

	[Fact]
	public void Resolves_classes_units_and_union_aliases_from_the_module()
	{
		var resolver = TypeTestSupport.ResolverFor(
			"type Point { } type Box<T> { } @type Base { } unit Meter { } unit! Second { } type id_value = [str, i32]");

		var point = Assert.IsType<ClassType>(resolver.Resolve(TypeTestSupport.ParseType("Point")));
		Assert.Equal("Point", point.ToString());
		Assert.Equal(SymbolKind.Class, point.Symbol.Kind);
		Assert.Empty(point.Arguments);

		var box = Assert.IsType<ClassType>(resolver.Resolve(TypeTestSupport.ParseType("Box<i32>")));
		Assert.Equal("Box<i32>", box.ToString());
		Assert.Equal(["T"], box.Symbol.GenericParameterNames);
		Assert.Equal<KernType>(PrimitiveType.I32, Assert.Single(box.Arguments));

		var baseType = Assert.IsType<ClassType>(resolver.Resolve(TypeTestSupport.ParseType("Base")));
		Assert.Equal(SymbolKind.AbstractClass, baseType.Symbol.Kind);

		var meter = Assert.IsType<UnitType>(resolver.Resolve(TypeTestSupport.ParseType("Meter")));
		Assert.Equal(SymbolKind.Unit, meter.Symbol.Kind);
		Assert.False(meter.IsImmutable);

		var second = Assert.IsType<UnitType>(resolver.Resolve(TypeTestSupport.ParseType("Second")));
		Assert.Equal(SymbolKind.ImmutableUnit, second.Symbol.Kind);
		Assert.True(second.IsImmutable);
		Assert.Equal("Second", second.ToString());

		var alias = resolver.Resolve(TypeTestSupport.ParseType("id_value"));
		Assert.Equal("[i32,str]", alias.ToString());
		Assert.IsType<UnionType>(alias);
		Assert.Empty(resolver.Diagnostics);
	}

	[Fact]
	public void Primitives_are_not_shadowed_by_module_declarations()
	{
		var resolver = TypeTestSupport.ResolverFor("type i32 { }");

		var type = resolver.Resolve(TypeTestSupport.ParseType("i32"));

		Assert.Same(PrimitiveType.I32, type);
		Assert.Empty(resolver.Diagnostics);
	}

	[Fact]
	public void Reports_missing_class_and_builtin_generic_arguments()
	{
		var box = TypeTestSupport.SingleDiagnostic("type Box<T> { }", "Box");
		Assert.Equal("KRN1002", box.Code);
		Assert.Equal("Тип 'Box' ожидает 1 аргументов типа, получено 0", box.Message);

		var tooMany = TypeTestSupport.SingleDiagnostic("type Box<T> { }", "Box<i32, str>");
		Assert.Equal("KRN1002", tooMany.Code);

		var list = TypeTestSupport.SingleDiagnostic("", "list<i32, str>");
		Assert.Equal("KRN1002", list.Code);
		Assert.Equal("Тип 'list' ожидает 1 аргументов типа, получено 2", list.Message);

		var map = TypeTestSupport.SingleDiagnostic("", "map<i32>");
		Assert.Equal("KRN1002", map.Code);
	}

	[Fact]
	public void Reports_arguments_on_non_generic_names()
	{
		var thing = TypeTestSupport.SingleDiagnostic("type Thing { }", "Thing<i32>");
		Assert.Equal("KRN1003", thing.Code);
		Assert.Equal("Тип 'Thing' не является generic", thing.Message);

		var alias = TypeTestSupport.SingleDiagnostic("type id_value = [i32, str]", "id_value<i32>");
		Assert.Equal("KRN1003", alias.Code);
	}

	[Fact]
	public void Reports_unknown_type()
	{
		var diagnostic = TypeTestSupport.SingleDiagnostic("", "Nope");

		Assert.Equal("KRN1001", diagnostic.Code);
		Assert.Equal("Неизвестный тип 'Nope'", diagnostic.Message);
		Assert.True(diagnostic.Span.Length > 0);
	}

	[Fact]
	public void Resolves_function_and_tuple_type_syntax()
	{
		var function = Assert.IsType<FunctionType>(TypeTestSupport.Resolve("", "fun(i32, str) -> bool"));
		Assert.Equal("fun(i32,str)->bool", function.ToString());
		Assert.Equal([PrimitiveType.I32, PrimitiveType.Str], function.Parameters);
		Assert.Same(PrimitiveType.Bool, function.ReturnType);

		var noReturn = Assert.IsType<FunctionType>(TypeTestSupport.Resolve("", "fun -> void"));
		Assert.Equal("fun()->void", noReturn.ToString());
		Assert.Same(PrimitiveType.Void, noReturn.ReturnType);

		var tuple = Assert.IsType<TupleType>(TypeTestSupport.Resolve("", "(i32 x, f64 y)"));
		Assert.Equal("(i32 x,f64 y)", tuple.ToString());
		Assert.Equal(["x", "y"], tuple.Elements.Select(element => element.Name));
		Assert.Equal([PrimitiveType.I32, PrimitiveType.F64], tuple.Elements.Select(element => element.Type));
	}

	[Fact]
	public void Resolves_type_parameters_from_a_function_declaration()
	{
		var parsed = Parser.Parse("fun largest<T[num]>(T left, T right) -> T { }");
		Assert.Empty(parsed.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(parsed.Tree.Declarations));
		var resolver = new TypeResolver(SymbolTable.Collect(parsed.Tree).Table);

		var scope = resolver.CreateScope(function.GenericParameters);
		var parameter = Assert.IsType<TypeParameterType>(resolver.Resolve(TypeTestSupport.ParseType("T"), scope));

		Assert.Equal("T", parameter.ToString());
		Assert.Equal([PrimitiveType.Num], parameter.Constraints);
		Assert.Empty(resolver.Diagnostics);
	}

	[Fact]
	public void Reports_invalid_tuple_arity_and_empty_union()
	{
		var span = new TextSpan(0, 0);
		var tupleResolver = TypeTestSupport.ResolverFor("");
		var invalidTuple = new TupleTypeSyntax(
			[new TupleTypeElementSyntax(NamedTypeSyntax.Plain("i32", span), null, span)], span);
		Assert.IsType<ErrorType>(tupleResolver.Resolve(invalidTuple));
		var tupleDiagnostic = Assert.Single(tupleResolver.Diagnostics);
		Assert.Equal("KRN1004", tupleDiagnostic.Code);
		Assert.Equal("Кортеж должен содержать не менее двух элементов", tupleDiagnostic.Message);

		var unionResolver = TypeTestSupport.ResolverFor("");
		Assert.IsType<ErrorType>(unionResolver.Resolve(new UnionTypeSyntax([], span)));
		Assert.Equal("KRN1005", Assert.Single(unionResolver.Diagnostics).Code);
	}

	[Fact]
	public void Populates_direct_bases_in_the_class_generic_scope()
	{
		var resolver = TypeTestSupport.ResolverFor(
			"type Root { } type Parent<T> { } type Child<T>[Parent<T>, Root] { } unit Meter { }");

		var child = Assert.IsType<ClassType>(resolver.Resolve(TypeTestSupport.ParseType("Child<i32>")));
		Assert.Empty(resolver.Diagnostics);
		Assert.Equal(["Parent<T>", "Root"], child.Symbol.Bases.Select(baseType => baseType.ToString()));
		Assert.IsType<ClassType>(child.Symbol.Bases[0]);

		var root = Assert.IsType<ClassType>(resolver.Resolve(TypeTestSupport.ParseType("Root")));
		Assert.Empty(root.Symbol.Bases);

		var meter = Assert.IsType<UnitType>(resolver.Resolve(TypeTestSupport.ParseType("Meter")));
		Assert.Empty(meter.Symbol.Bases);
	}

	[Fact]
	public void Breaks_inheritance_cycles_with_one_diagnostic_per_symbol()
	{
		var resolver = TypeTestSupport.ResolverFor("type A[B] { } type B[A] { }");

		var a = Assert.IsType<ClassType>(resolver.Resolve(TypeTestSupport.ParseType("A")));

		var diagnostic = Assert.Single(resolver.Diagnostics);
		Assert.Equal("KRN1006", diagnostic.Code);
		Assert.Equal("Циклическое наследование типа 'A'", diagnostic.Message);
		Assert.Equal(["B"], a.Symbol.Bases.Select(baseType => baseType.ToString()));
		var b = Assert.IsType<ClassType>(Assert.Single(a.Symbol.Bases));
		Assert.Empty(b.Symbol.Bases);
	}

	[Fact]
	public void Self_inheritance_is_a_cycle_and_keeps_no_bases()
	{
		var resolver = TypeTestSupport.ResolverFor("type Self[Self] { }");

		var self = Assert.IsType<ClassType>(resolver.Resolve(TypeTestSupport.ParseType("Self")));

		Assert.Equal("KRN1006", Assert.Single(resolver.Diagnostics).Code);
		Assert.Empty(self.Symbol.Bases);
	}

	[Fact]
	public void IsNullable_wraps_the_resolved_type()
	{
		var resolver = TypeTestSupport.ResolverFor("");
		var nullable = resolver.Resolve(TypeTestSupport.ParseType("i32?"));

		Assert.Equal("i32?", nullable.ToString());
		Assert.Equal(NullableType.Create(PrimitiveType.I32), nullable);
		Assert.Empty(resolver.Diagnostics);
	}
}
