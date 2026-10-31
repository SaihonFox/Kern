using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Syntax;

public sealed class ParserTests
{
	[Fact]
	public void Parses_static_type_and_function_members()
	{
		var result = Parser.Parse("type Math { #type Cache {} #fun twice(i32 n) -> i32 n * 2 #i32 count = 0 }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.Equal(3, type.DeclaredMembers.Count);
		Assert.IsType<TypeDeclaration>(type.DeclaredMembers[0]);
		Assert.True(Assert.IsType<TypeDeclaration>(type.DeclaredMembers[0]).IsStatic);
		Assert.Equal("twice", Assert.IsType<FunctionDeclaration>(type.DeclaredMembers[1]).Name);
		Assert.True(Assert.IsType<FunctionDeclaration>(type.DeclaredMembers[1]).IsStatic);
		var field = Assert.IsType<FieldMemberNode>(type.DeclaredMembers[2]);
		Assert.Equal("count", field.Name);
		Assert.True(field.IsStatic);
	}

	[Fact]
	public void Parses_required_top_level_field()
	{
		var result = Parser.Parse("i32! total");

		Assert.Empty(result.Diagnostics);
		var field = Assert.IsType<GlobalFieldDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.Equal("i32!", field.Type.ToString());
		Assert.Equal("total", field.Name);
		Assert.True(field.IsRequired);
	}

	[Fact]
	public void Parses_inferred_immutable_top_level_field()
	{
		var result = Parser.Parse("var! limit = 5");

		Assert.Empty(result.Diagnostics);
		var field = Assert.IsType<GlobalFieldDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.Equal("var", field.Type.ToString());
		Assert.Equal("limit", field.Name);
		Assert.True(field.IsInferred);
		Assert.IsType<IntegerLiteralExpression>(field.Initializer);
	}

	[Fact]
	public void Parses_optional_top_level_project_declaration()
	{
		var declared = Parser.Parse("project geometry.shapes:\nuse kern.text");
		var omitted = Parser.Parse("use kern.text");

		Assert.Empty(declared.Diagnostics);
		Assert.Equal("geometry.shapes", declared.Tree.Project);
		Assert.IsType<UseDeclaration>(Assert.Single(declared.Tree.Declarations));
		Assert.Empty(omitted.Diagnostics);
		Assert.Null(omitted.Tree.Project);
	}

	[Fact]
	public void Reports_malformed_project_declaration()
	{
		var missingColon = Parser.Parse("project geometry.shapes\nuse kern.text");
		var notAtStart = Parser.Parse("use kern.text\nproject geometry.shapes:");

		Assert.NotEmpty(missingColon.Diagnostics);
		Assert.NotEmpty(notAtStart.Diagnostics);
	}

	[Fact]
	public void Parses_operator_and_leading_dot_continuations_across_lines()
	{
		var binary = Parser.ParseExpression("1 +\n2");
		var chain = Parser.ParseExpression("items\n.map(fun(i32 n) -> i32 n)");

		Assert.Empty(binary.Diagnostics);
		Assert.Equal("+", Assert.IsType<BinaryExpression>(binary.Expression).Operator);
		Assert.Empty(chain.Diagnostics);
		Assert.Equal("map", Assert.IsType<MemberAccessExpression>(
			Assert.IsType<CallExpression>(chain.Expression).Target).Member);
	}

	[Fact]
	public void Parses_binary_expression_with_precedence()
	{
		var result = Parser.ParseExpression("1 + 2 * (3 + 4)");

		Assert.Empty(result.Diagnostics);
		var add = Assert.IsType<BinaryExpression>(result.Expression);
		Assert.Equal("+", add.Operator);
		Assert.IsType<IntegerLiteralExpression>(add.Left);
		var multiply = Assert.IsType<BinaryExpression>(add.Right is ParenthesizedExpression parenthesized
			? parenthesized.Expression
			: add.Right);
		Assert.Equal("*", multiply.Operator);
	}

	[Fact]
	public void Parses_bare_unit_and_unit_body()
	{
		var bare = Parser.Parse("unit Red");
		var withBody = Parser.Parse("unit Point { i32 x }");

		Assert.Empty(bare.Diagnostics);
		Assert.Empty(withBody.Diagnostics);
		var bareUnit = Assert.IsType<UnitDeclaration>(Assert.Single(bare.Tree.Declarations));
		Assert.Equal("Red", bareUnit.Name);
		Assert.Empty(bareUnit.PrimaryParameters);
		var bodyUnit = Assert.IsType<UnitDeclaration>(Assert.Single(withBody.Tree.Declarations));
		Assert.Single(bodyUnit.Members!);
	}

	[Fact]
	public void Parses_unit_primary_constructor_and_immutable_generic_header()
	{
		var primary = Parser.Parse("unit Point(f64 x, f64 y)");
		var immutable = Parser.Parse("unit! IpAddress(str address)");
		var generic = Parser.Parse("unit Box<T>(T value)");

		Assert.Empty(primary.Diagnostics);
		Assert.Empty(immutable.Diagnostics);
		Assert.Empty(generic.Diagnostics);
		var point = Assert.IsType<UnitDeclaration>(Assert.Single(primary.Tree.Declarations));
		Assert.Equal(["x", "y"], point.PrimaryParameters.Select(parameter => parameter.Name));
		Assert.Equal(["f64", "f64"], point.PrimaryParameters.Select(parameter => parameter.Type?.ToString()));
		var address = Assert.IsType<UnitDeclaration>(Assert.Single(immutable.Tree.Declarations));
		Assert.True(address.IsImmutable);
		var box = Assert.IsType<UnitDeclaration>(Assert.Single(generic.Tree.Declarations));
		Assert.Equal(["T"], box.DeclaredGenericParameters.Select(parameter => parameter.Name));
		Assert.Equal("T", Assert.Single(box.PrimaryParameters).Type?.ToString());
	}

	[Fact]
	public void Parses_typed_and_inferred_resource_use_statements()
	{
		var result = Parser.Parse("fun main {\nuse stream s = make_stream()\nuse var g = await am.lock()\n}");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		var typed = Assert.IsType<UseStatement>(function.Body.Statements[0]);
		Assert.Equal("stream", typed.Type?.ToString());
		Assert.Equal("s", typed.Name);
		Assert.False(typed.IsInferred);
		Assert.IsType<CallExpression>(typed.Initializer);

		var inferred = Assert.IsType<UseStatement>(function.Body.Statements[1]);
		Assert.Null(inferred.Type);
		Assert.Equal("g", inferred.Name);
		Assert.True(inferred.IsInferred);
		var awaitExpression = Assert.IsType<UnaryExpression>(inferred.Initializer);
		Assert.Equal("await", awaitExpression.Operator);
		Assert.IsType<CallExpression>(awaitExpression.Operand);
	}

	[Fact]
	public void Parses_union_alias_variants_and_recovers_from_malformed_forms()
	{
		var union = Parser.Parse("type id_value = [i32, str]");
		var malformed = Parser.Parse("type broken = [i32 str]");
		var normal = Parser.Parse("type user");

		Assert.Empty(union.Diagnostics);
		var alias = Assert.IsType<TypeDeclaration>(Assert.Single(union.Tree.Declarations));
		Assert.Equal("id_value", alias.Name);
		Assert.Equal(["i32", "str"], alias.DeclaredUnionVariants.Select(variant => variant.ToString()));

		Assert.NotEmpty(malformed.Diagnostics);
		Assert.IsType<TypeDeclaration>(Assert.Single(malformed.Tree.Declarations));

		Assert.Empty(normal.Diagnostics);
		var regularType = Assert.IsType<TypeDeclaration>(Assert.Single(normal.Tree.Declarations));
		Assert.Equal("user", regularType.Name);
		Assert.Empty(regularType.DeclaredUnionVariants);
	}

	[Fact]
	public void Parses_selected_imports_visibility_and_async_function_prefixes()
	{
		var result = Parser.Parse("use geometry.shapes[Shape, area]\npub async fun main { }\npriv type hidden { }");

		Assert.Empty(result.Diagnostics);
		Assert.Equal(3, result.Tree.Declarations.Count);
		Assert.Equal("geometry.shapes", Assert.IsType<UseDeclaration>(result.Tree.Declarations[0]).Path);
		Assert.Equal("main", Assert.IsType<FunctionDeclaration>(result.Tree.Declarations[1]).Name);
		Assert.Equal("hidden", Assert.IsType<TypeDeclaration>(result.Tree.Declarations[2]).Name);
	}

	[Fact]
	public void Parses_tuple_declaration_with_typed_rest_binding()
	{
		var result = Parser.Parse("fun main { (i32 first, list<i32> ..rest, _) = nums }");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		var declaration = Assert.IsType<TupleDeclarationStatement>(Assert.Single(function.Body.Statements));
		Assert.Equal("first", declaration.Bindings[0].Name);
		Assert.Equal("rest", declaration.Bindings[1].RestName);
		Assert.Equal("list<i32>", declaration.Bindings[1].Type?.ToString());
		Assert.True(declaration.Bindings[2].IsIgnored);
	}

	[Fact]
	public void Parses_abstract_type_members_with_attributes()
	{
		var result = Parser.Parse("@type PostsApi { #[get(\"/posts\")] @fun by_user(i64 user_id) -> task<result<list<Post>, str>> }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.IsType<FunctionDeclaration>(Assert.Single(type.Members!));
	}

	[Fact]
	public void Parses_async_lambda_argument_and_do_while_loop()
	{
		var result = Parser.Parse("async fun main { run(async fun { work() }) loop { tick() }: keep_going }");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.Equal(2, function.Body.Statements.Count);
		Assert.IsType<ExpressionStatement>(function.Body.Statements[1]);
	}

	[Fact]
	public void Parses_default_parameters_and_constrained_generic_function()
	{
		var defaults = Parser.Parse("fun greet(str name = \"Guest\", i32 age = 18) -> str name");
		var generic = Parser.Parse("fun largest<T[num]>(T left, T right) -> T if left > right { left } else { right }");

		Assert.Empty(defaults.Diagnostics);
		Assert.Empty(generic.Diagnostics);
		var greet = Assert.IsType<FunctionDeclaration>(Assert.Single(defaults.Tree.Declarations));
		Assert.IsType<StringLiteralExpression>(greet.Parameters[0].DefaultValue);
		Assert.IsType<IntegerLiteralExpression>(greet.Parameters[1].DefaultValue);
		Assert.Equal("largest", Assert.IsType<FunctionDeclaration>(Assert.Single(generic.Tree.Declarations)).Name);
	}

	[Fact]
	public void Parses_stage_one_macro_elvis_and_generic_loop_forms()
	{
		var macro = Parser.ParseExpression("sq!(3 + 1)");
		var elvis = Parser.ParseExpression("text.to_i32() ?: ret Err(\"bad\")");
		var loop = Parser.Parse("fun main { loop (str who, i32 years) in ages println(who) }");

		Assert.Empty(macro.Diagnostics);
		Assert.True(Assert.IsType<CallExpression>(macro.Expression).IsMacroInvocation);
		Assert.Empty(elvis.Diagnostics);
		Assert.Empty(loop.Diagnostics);
	}

	[Fact]
	public void Parses_async_override_member_named_next()
	{
		var result = Parser.Parse("type ticker { ~async fun next -> i32? { await delay(1) nil } }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.Equal("next", Assert.IsType<FunctionDeclaration>(Assert.Single(type.Members!)).Name);
	}

	[Fact]
	public void Parses_typed_function_and_basic_declarations()
	{
		var result = Parser.Parse("fun add(i32 a, i32 b) -> i32 { a + b } type user unit meter use kern.text");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(result.Tree.Declarations[0]);
		Assert.Equal("i32", function.Parameters[0].Type?.ToString());
		Assert.Equal("i32", function.ReturnType?.ToString());
		Assert.IsType<TypeDeclaration>(result.Tree.Declarations[1]);
		Assert.IsType<UnitDeclaration>(result.Tree.Declarations[2]);
		var use = Assert.IsType<UseDeclaration>(result.Tree.Declarations[3]);
		Assert.Equal("kern.text", use.Path);
	}

	[Fact]
	public void Parses_parent_member_selection_declarations()
	{
		var result = Parser.Parse("type C()[A] { fun $[A].hello(i32 x) -> str\nstr $[A].name }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		var method = Assert.IsType<ParentMethodSelectionNode>(type.DeclaredMembers[0]);
		Assert.Equal("A", method.ParentType);
		Assert.Equal("hello", method.Name);
		Assert.Equal("str", method.ReturnType?.ToString());
		Assert.Equal("i32", Assert.Single(method.Parameters).Type?.ToString());
		var property = Assert.IsType<ParentFieldSelectionNode>(type.DeclaredMembers[1]);
		Assert.Equal("A", property.ParentType);
		Assert.Equal("str", property.Type.ToString());
		Assert.Equal("name", property.Name);
	}

	[Fact]
	public void Parses_expression_bodied_operator_with_self_members()
	{
		var result = Parser.Parse("unit Point(f64 x, f64 y) { op *(f64 k) -> Point Point($x * k, $y * k) }");

		Assert.Empty(result.Diagnostics);
		var unit = Assert.IsType<UnitDeclaration>(Assert.Single(result.Tree.Declarations));
		var operation = Assert.IsType<OperatorMemberNode>(Assert.Single(unit.Members!, member => member is OperatorMemberNode));
		var body = Assert.IsType<BlockStatement>(operation.Body);
		var call = Assert.IsType<CallExpression>(Assert.IsType<ExpressionStatement>(Assert.Single(body.Statements)).Expression);
		Assert.Equal("Point", Assert.IsType<IdentifierExpression>(call.Target).Name);
		Assert.Equal(2, call.Arguments.Count);
		foreach (var argument in call.Arguments)
		{
			var multiply = Assert.IsType<BinaryExpression>(argument);
			var selfMember = Assert.IsType<MemberAccessExpression>(multiply.Left);
			Assert.IsType<SelfExpression>(selfMember.Target);
		}
	}

	[Fact]
	public void Parses_self_members_in_unit_property_and_operator_expressions()
	{
		var property = Parser.ParseExpression("$x + $y");
		var operatorBody = Parser.ParseExpression("Point($x + other.x, $y + other.y)");

		Assert.Empty(property.Diagnostics);
		var propertySum = Assert.IsType<BinaryExpression>(property.Expression);
		var selfX = Assert.IsType<MemberAccessExpression>(propertySum.Left);
		Assert.IsType<SelfExpression>(selfX.Target);
		Assert.Equal("x", selfX.Member);
		var selfY = Assert.IsType<MemberAccessExpression>(propertySum.Right);
		Assert.IsType<SelfExpression>(selfY.Target);
		Assert.Equal("y", selfY.Member);

		Assert.Empty(operatorBody.Diagnostics);
		var call = Assert.IsType<CallExpression>(operatorBody.Expression);
		var firstArgument = Assert.IsType<BinaryExpression>(call.Arguments[0]);
		Assert.IsType<MemberAccessExpression>(firstArgument.Left);
		Assert.IsType<SelfExpression>(Assert.IsType<MemberAccessExpression>(firstArgument.Left).Target);
		Assert.IsType<MemberAccessExpression>(firstArgument.Right);
		var secondArgument = Assert.IsType<BinaryExpression>(call.Arguments[1]);
		Assert.IsType<SelfExpression>(Assert.IsType<MemberAccessExpression>(secondArgument.Left).Target);
		Assert.IsType<MemberAccessExpression>(secondArgument.Right);
	}

	[Fact]
	public void Parses_tuple_expressions_and_grouping()
	{
		var tuple = Parser.ParseExpression("(10, \"hello\")");
		var expression = Parser.ParseExpression("(lo, hi)");
		var grouping = Parser.ParseExpression("(value)");

		Assert.Empty(tuple.Diagnostics);
		var literal = Assert.IsType<TupleExpression>(tuple.Expression);
		Assert.IsType<IntegerLiteralExpression>(literal.Items[0]);
		Assert.IsType<StringLiteralExpression>(literal.Items[1]);
		Assert.Empty(expression.Diagnostics);
		Assert.Equal(2, Assert.IsType<TupleExpression>(expression.Expression).Items.Count);
		Assert.Empty(grouping.Diagnostics);
		Assert.IsType<ParenthesizedExpression>(grouping.Expression);
	}

	[Fact]
	public void Parses_typed_named_and_destructuring_tuple_declarations()
	{
		var result = Parser.Parse("fun main {\n(i32, str) pair = (10, \"hello\")\n(i32 x, i32 y) point = (3, 4)\n(i32 n, str s) = pair\n(i32 a, _, str c) = pair\n}");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		var statements = function.Body.Statements;
		var typed = Assert.IsType<TupleDeclarationStatement>(statements[0]);
		Assert.Equal("pair", typed.Name);
		Assert.Equal(["i32", "str"], typed.Bindings.Select(binding => binding.Type?.ToString()));
		Assert.IsType<TupleExpression>(typed.Initializer);
		var named = Assert.IsType<TupleDeclarationStatement>(statements[1]);
		Assert.Equal("point", named.Name);
		Assert.Equal(["x", "y"], named.Bindings.Select(binding => binding.Name));
		var destructured = Assert.IsType<TupleDeclarationStatement>(statements[2]);
		Assert.Null(destructured.Name);
		Assert.Equal(["n", "s"], destructured.Bindings.Select(binding => binding.Name));
		var ignored = Assert.IsType<TupleDeclarationStatement>(statements[3]);
		Assert.True(ignored.Bindings[1].IsIgnored);
		Assert.Null(ignored.Bindings[1].Name);
	}

	[Fact]
	public void Parses_tuple_return_types_with_named_and_unnamed_components()
	{
		var named = Parser.Parse("fun bounds(i32 value) -> (i32 low, i32 high) { value }");
		var unnamed = Parser.Parse("fun pair() -> (i32, i32) { 1 }");

		Assert.Empty(named.Diagnostics);
		var namedFunction = Assert.IsType<FunctionDeclaration>(Assert.Single(named.Tree.Declarations));
		var tupleType = Assert.IsType<TupleTypeSyntax>(namedFunction.ReturnType);
		Assert.Equal(2, tupleType.Elements.Count);
		Assert.Equal("i32", tupleType.Elements[0].Type.ToString());
		Assert.Equal("low", tupleType.Elements[0].Name);
		Assert.Equal("i32", tupleType.Elements[1].Type.ToString());
		Assert.Equal("high", tupleType.Elements[1].Name);
		Assert.Equal("(i32 low,i32 high)", tupleType.ToString());

		Assert.Empty(unnamed.Diagnostics);
		var unnamedType = Assert.IsType<TupleTypeSyntax>(
			Assert.IsType<FunctionDeclaration>(Assert.Single(unnamed.Tree.Declarations)).ReturnType);
		Assert.Equal(2, unnamedType.Elements.Count);
		Assert.All(unnamedType.Elements, element => Assert.Null(element.Name));
		Assert.Equal("(i32,i32)", unnamedType.ToString());
	}

	[Fact]
	public void Parses_simple_function_declaration()
	{
		var result = Parser.Parse("fun add(a, b) { a + b; }");

		Assert.Empty(result.Diagnostics);
		var function = Assert.Single(result.Tree.Declarations);
		var declaration = Assert.IsType<FunctionDeclaration>(function);
		Assert.Equal("add", declaration.Name);
		Assert.Equal(2, declaration.Parameters.Count);
		Assert.Single(declaration.Body.Statements);
	}

	[Fact]
	public void Parses_nullable_local_declaration_without_initializer()
	{
		var result = Parser.Parse("fun f { i32? value; }");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		var declaration = Assert.IsType<LocalDeclarationStatement>(Assert.Single(function.Body.Statements));
		Assert.Equal("i32?", declaration.Type?.ToString());
		Assert.Equal("value", declaration.Name);
		Assert.Null(declaration.Initializer);
	}

	[Fact]
	public void Parses_typed_local_declaration_without_initializer()
	{
		var result = Parser.Parse("fun f { i32 value\nprintln(value) }");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.Equal(2, function.Body.Statements.Count);
		var declaration = Assert.IsType<LocalDeclarationStatement>(function.Body.Statements[0]);
		Assert.Equal("i32", declaration.Type?.ToString());
		Assert.Null(declaration.Initializer);
	}

	[Fact]
	public void Parses_expression_bodied_function_without_braces()
	{
		var result = Parser.Parse("fun get_five -> i32 5");

		Assert.Empty(result.Diagnostics);
		var declaration = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.Equal("i32", declaration.ReturnType?.ToString());
		Assert.IsType<IntegerLiteralExpression>(((ExpressionStatement)Assert.Single(declaration.Body.Statements)).Expression);
	}

	[Fact]
	public void Parses_control_flow_and_calls()
	{
		var result = Parser.Parse("fun run(i32 value) { if value { print(value, 1) } else { next } loop { ret print(value) stop } }");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		var conditional = Assert.IsType<IfExpression>(((ExpressionStatement)function.Body.Statements[0]).Expression);
		Assert.NotNull(conditional.Else);
		var then = Assert.IsType<BlockStatement>(conditional.Then);
		Assert.IsType<CallExpression>(((ExpressionStatement)then.Statements[0]).Expression);
		Assert.IsType<LoopStatement>(function.Body.Statements[1]);
		var loop = (LoopStatement)function.Body.Statements[1];
		Assert.IsType<ReturnStatement>(loop.Body.Statements[0]);
		Assert.IsType<StopStatement>(loop.Body.Statements[1]);
	}

	[Fact]
	public void Parses_positional_nested_constructor_patterns()
	{
		var result = Parser.Parse("fun f(value) { match value { IpAddress(addr) => addr; Ok(Err(error)) => error } }");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		var match = Assert.IsType<MatchExpression>(((ExpressionStatement)function.Body.Statements[0]).Expression);
		var address = Assert.IsType<ConstructorPattern>(match.Arms[0].Pattern);
		Assert.Equal("IpAddress", address.Name);
		Assert.Equal("addr", Assert.IsType<IdentifierPattern>(Assert.Single(address.Arguments)).Name);
		var nested = Assert.IsType<ConstructorPattern>(match.Arms[1].Pattern);
		var inner = Assert.IsType<ConstructorPattern>(Assert.Single(nested.Arguments));
		Assert.Equal("Err", inner.Name);
		Assert.Equal("error", Assert.IsType<IdentifierPattern>(Assert.Single(inner.Arguments)).Name);
	}

	[Fact]
	public void Parses_structural_named_patterns_and_rest()
	{
		var result = Parser.Parse("fun f(value) { match value { Point(x: 0, y: var y, ..rest) => y; User(role: \"admin\", ..) => 1 } }");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		var match = Assert.IsType<MatchExpression>(((ExpressionStatement)function.Body.Statements[0]).Expression);
		var point = Assert.IsType<StructuralPattern>(match.Arms[0].Pattern);
		Assert.Equal("Point", point.Type.ToString());
		Assert.True(point.HasRest);
		Assert.Equal(2, point.Fields.Count);
		Assert.IsType<LiteralPattern>(point.Fields[0].Pattern);
		Assert.Equal("y", Assert.IsType<BindingPattern>(point.Fields[1].Pattern).Name);
		Assert.Equal("rest", point.RestName);
		var user = Assert.IsType<StructuralPattern>(match.Arms[1].Pattern);
		Assert.True(user.HasRest);
		Assert.IsType<LiteralPattern>(user.Fields[0].Pattern);
	}

	[Fact]
	public void Parses_match_guards_with_if_and_colon()
	{
		var result = Parser.Parse("fun f(value) { match value { i32 n if n > 10 => n; i32 m: m >= 0 => m; _ => 0 } }");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		var match = Assert.IsType<MatchExpression>(((ExpressionStatement)function.Body.Statements[0]).Expression);
		Assert.IsType<BinaryExpression>(match.Arms[0].Guard);
		Assert.Equal(">", Assert.IsType<BinaryExpression>(match.Arms[0].Guard).Operator);
		Assert.IsType<BinaryExpression>(match.Arms[1].Guard);
		Assert.Null(match.Arms[2].Guard);
	}

	[Fact]
	public void Parses_targetless_pure_guard_match()
	{
		var result = Parser.ParseExpression("match { score > 0 => 1; _ => 0 }");

		Assert.Empty(result.Diagnostics);
		var match = Assert.IsType<MatchExpression>(result.Expression);
		Assert.True(match.IsTargetless);
		Assert.Equal(2, match.Arms.Count);
		Assert.IsType<BinaryExpression>(Assert.IsType<ExpressionPattern>(match.Arms[0].Pattern).Expression);
	}

	[Fact]
	public void Recovers_from_malformed_pattern_and_keeps_later_arm()
	{
		var result = Parser.Parse("fun f(value) { match value { Point(x: ) => 0; Ok(v) => v } }");

		Assert.NotEmpty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		var match = Assert.IsType<MatchExpression>(((ExpressionStatement)function.Body.Statements[0]).Expression);
		Assert.Equal(2, match.Arms.Count);
		Assert.Equal("Ok", Assert.IsType<ConstructorPattern>(match.Arms[1].Pattern).Name);
	}

	[Fact]
	public void Parses_match_literal_identifier_and_wildcard_arms()
	{
		var result = Parser.Parse("fun classify(i32 value) { match value { 1 => print(\"one\"); name => print(name); _ => print(\"other\") } }");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		var match = Assert.IsType<MatchExpression>(((ExpressionStatement)function.Body.Statements[0]).Expression);
		Assert.Equal(3, match.Arms.Count);
		Assert.IsType<LiteralPattern>(match.Arms[0].Pattern);
		Assert.IsType<IdentifierPattern>(match.Arms[1].Pattern);
		Assert.IsType<WildcardPattern>(match.Arms[2].Pattern);
	}

	[Fact]
	public void Parses_type_patterns_with_bindings()
	{
		var result = Parser.Parse("fun describe(id_value id) -> str match id { str s => f\"строка ${s}\"; i32 n => f\"число ${n}\" }");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		var match = Assert.IsType<MatchExpression>(((ExpressionStatement)Assert.Single(function.Body.Statements)).Expression);
		var stringPattern = Assert.IsType<TypePattern>(match.Arms[0].Pattern);
		Assert.Equal("str", stringPattern.Type.ToString());
		Assert.Equal("s", stringPattern.Name);
		var integerPattern = Assert.IsType<TypePattern>(match.Arms[1].Pattern);
		Assert.Equal("i32", integerPattern.Type.ToString());
		Assert.Equal("n", integerPattern.Name);
	}

	[Fact]
	public void Parses_lambda_and_conditional_member_access()
	{
		var result = Parser.ParseExpression("fun(i32 x) -> i32 x + 1");
		var memberResult = Parser.ParseExpression("value?.member");

		Assert.Empty(result.Diagnostics);
		var lambda = Assert.IsType<LambdaExpression>(result.Expression);
		Assert.Equal("i32", lambda.ReturnType?.ToString());
		Assert.Single(lambda.Parameters);
		Assert.IsType<BinaryExpression>(lambda.Body);
		Assert.Empty(memberResult.Diagnostics);
		var member = Assert.IsType<MemberAccessExpression>(memberResult.Expression);
		Assert.True(member.IsConditional);
		Assert.Equal("member", member.Member);
	}

	[Fact]
	public void Parses_await_as_a_prefix_operator_with_unary_precedence()
	{
		var result = Parser.ParseExpression("await first + await second");

		Assert.Empty(result.Diagnostics);
		var addition = Assert.IsType<BinaryExpression>(result.Expression);
		Assert.Equal("+", addition.Operator);
		Assert.Equal("await", Assert.IsType<UnaryExpression>(addition.Left).Operator);
		Assert.Equal("await", Assert.IsType<UnaryExpression>(addition.Right).Operator);

		var callResult = Parser.ParseExpression("run(await task)");
		Assert.Empty(callResult.Diagnostics);
		var call = Assert.IsType<CallExpression>(callResult.Expression);
		Assert.Equal("await", Assert.IsType<UnaryExpression>(Assert.Single(call.Arguments)).Operator);
	}

	[Fact]
	public void Parses_ranges_and_if_expression()
	{
		var range = Parser.ParseExpression("a..=b:step");
		var conditional = Parser.ParseExpression("if condition { when_true } else { when_false }");

		Assert.Empty(range.Diagnostics);
		var rangeExpression = Assert.IsType<RangeExpression>(range.Expression);
		Assert.True(rangeExpression.IsInclusive);
		Assert.NotNull(rangeExpression.Step);
		Assert.Empty(conditional.Diagnostics);
		var ifExpression = Assert.IsType<IfExpression>(conditional.Expression);
		Assert.NotNull(ifExpression.Else);
	}

	[Fact]
	public void Parses_statement_if_without_else_and_requires_else_for_values()
	{
		var statement = Parser.Parse("fun run(value) { if value { print(value) } }");
		var expression = Parser.ParseExpression("if value { 1 }");

		Assert.Empty(statement.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(statement.Tree.Declarations));
		var statementIf = Assert.IsType<IfExpression>(((ExpressionStatement)function.Body.Statements[0]).Expression);
		Assert.Null(statementIf.Else);
		Assert.Contains(expression.Diagnostics, diagnostic => diagnostic.Code == "PAR007");
	}

	[Fact]
	public void Parses_else_if_chain_as_nested_if_expressions()
	{
		var result = Parser.ParseExpression("if first { 1 } else if second { 2 } else { 3 }");

		Assert.Empty(result.Diagnostics);
		var outer = Assert.IsType<IfExpression>(result.Expression);
		var nested = Assert.IsType<IfExpression>(outer.Else);
		Assert.Equal("second", Assert.IsType<IdentifierExpression>(nested.Condition).Name);
		Assert.NotNull(nested.Else);
	}

	[Fact]
	public void Parses_attributes_type_members_properties_and_ctor()
	{
		var result = Parser.Parse("#[serializable] @type Counter { !i32 _val i32 val { get _val set(value) _val = value } i32 doubled => _val * 2 ctor(i32 value) : $(value) }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.True(type.IsAbstract);
		Assert.Single(type.DeclaredAttributes);
		Assert.Equal(4, type.DeclaredMembers.Count);
		Assert.IsType<FieldMemberNode>(type.DeclaredMembers[0]);
		Assert.IsType<PropertyMemberNode>(type.DeclaredMembers[1]);
		Assert.IsType<PropertyMemberNode>(type.DeclaredMembers[2]);
		var ctor = Assert.IsType<ConstructorMemberNode>(type.DeclaredMembers[3]);
		Assert.Equal("value", Assert.IsType<IdentifierExpression>(Assert.Single(ctor.Chain!.Arguments)).Name);
	}
	[Fact]
	public void Parses_locals_collections_indexes_and_loop_forms()
	{
		var result = Parser.Parse("fun main { var xs = [1, 2, 3] list<i32> ys = xs loop var x in ys println(x) }");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.IsType<LocalDeclarationStatement>(function.Body.Statements[0]);
		var typed = Assert.IsType<LocalDeclarationStatement>(function.Body.Statements[1]);
		Assert.Equal("list<i32>", typed.Type?.ToString());
		var loop = Assert.IsType<LoopStatement>(function.Body.Statements[2]);
		Assert.Single(loop.Body.Statements);
	}

	[Fact]
	public void Parses_multiple_typed_local_declarations()
	{
		var result = Parser.Parse("fun main { i32 n1 = 397, n2 = 4, n3 }");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		var declaration = Assert.IsType<LocalDeclarationStatement>(Assert.Single(function.Body.Statements));
		Assert.Equal("i32", declaration.Type?.ToString());
		Assert.Equal("n1", declaration.Name);
		Assert.Equal("397", Assert.IsType<IntegerLiteralExpression>(declaration.Initializer).Text);
		Assert.Equal(["n2", "n3"], declaration.DeclaredBindings.Select(binding => binding.Name));
		Assert.Equal("4", Assert.IsType<IntegerLiteralExpression>(declaration.DeclaredBindings[0].Initializer).Text);
		Assert.Null(declaration.DeclaredBindings[1].Initializer);
	}

	[Fact]
	public void Parses_inferred_dictionary_literals()
	{
		var result = Parser.Parse("fun main { dict<str, i32> ages = [\"аня\": 25, \"борис\": 31] }");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		var declaration = Assert.IsType<LocalDeclarationStatement>(Assert.Single(function.Body.Statements));
		var dictionary = Assert.IsType<DictionaryExpression>(declaration.Initializer);
		Assert.Equal(2, dictionary.Entries.Count);
		Assert.IsType<StringLiteralExpression>(dictionary.Entries[0].Key);
		Assert.IsType<IntegerLiteralExpression>(dictionary.Entries[0].Value);
		Assert.IsType<StringLiteralExpression>(dictionary.Entries[1].Key);
	}

	[Fact]
	public void Parses_list_spread_items()
	{
		var result = Parser.ParseExpression("[1, ..another_list, 3]");

		Assert.Empty(result.Diagnostics);
		var list = Assert.IsType<CollectionExpression>(result.Expression);
		Assert.Equal(3, list.Items.Count);
		Assert.IsType<IntegerLiteralExpression>(list.Items[0]);
		Assert.IsType<ListSpreadExpression>(list.Items[1]);
		Assert.IsType<IntegerLiteralExpression>(list.Items[2]);
	}

	[Fact]
	public void Parses_list_spread_items_at_any_position_and_multiple_spreads()
	{
		var result = Parser.ParseExpression("[..first, 1, ..middle, 2, ..last]");

		Assert.Empty(result.Diagnostics);
		var list = Assert.IsType<CollectionExpression>(result.Expression);
		Assert.Equal(5, list.Items.Count);
		Assert.All([list.Items[0], list.Items[2], list.Items[4]], item => Assert.IsType<ListSpreadExpression>(item));
		Assert.IsType<IntegerLiteralExpression>(list.Items[1]);
		Assert.IsType<IntegerLiteralExpression>(list.Items[3]);
	}

	[Fact]
	public void Parses_explicitly_typed_collection_literals()
	{
		var list = Parser.ParseExpression("<i32>[1, 2]");
		var dictionary = Parser.ParseExpression("<str: i32>[\"k\": 1]");
		var emptyList = Parser.ParseExpression("<i32>[]");
		var emptyDictionary = Parser.ParseExpression("<str: bool>[]");

		Assert.Empty(list.Diagnostics);
		var typedList = Assert.IsType<TypedCollectionExpression>(list.Expression);
		Assert.Equal("i32", typedList.ElementType?.ToString());
		Assert.False(typedList.IsDictionary);
		Assert.Equal(2, typedList.Items.Count);

		Assert.Empty(dictionary.Diagnostics);
		var typedDictionary = Assert.IsType<TypedCollectionExpression>(dictionary.Expression);
		Assert.True(typedDictionary.IsDictionary);
		Assert.Equal("str", typedDictionary.KeyType?.ToString());
		Assert.Equal("i32", typedDictionary.ValueType?.ToString());
		Assert.Single(typedDictionary.Entries);

		Assert.Empty(emptyList.Diagnostics);
		Assert.Empty(Assert.IsType<TypedCollectionExpression>(emptyList.Expression).Items);
		Assert.Empty(emptyDictionary.Diagnostics);
		Assert.Empty(Assert.IsType<TypedCollectionExpression>(emptyDictionary.Expression).Entries);
	}

	[Fact]
	public void Parses_index_expression()
	{
		var result = Parser.ParseExpression("items[1..3]");

		Assert.Empty(result.Diagnostics);
		var index = Assert.IsType<IndexExpression>(result.Expression);
		Assert.Single(index.Arguments);
		Assert.IsType<RangeExpression>(index.Arguments[0]);
	}
	[Fact]
	public void Parses_unit_primary_constructor_headers()
	{
		var result = Parser.Parse("unit Point(f64 x, f64 y) unit! ImmutablePoint(i32 x, i32 y) unit Box<T>(T value)");

		Assert.Empty(result.Diagnostics);
		var point = Assert.IsType<UnitDeclaration>(result.Tree.Declarations[0]);
		Assert.Equal("Point", point.Name);
		Assert.False(point.IsImmutable);
		Assert.Empty(point.DeclaredGenericParameters);
		Assert.Equal(2, point.PrimaryParameters.Count);
		Assert.Equal("f64", point.PrimaryParameters[0].Type?.ToString());
		Assert.Equal("x", point.PrimaryParameters[0].Name);

		var immutable = Assert.IsType<UnitDeclaration>(result.Tree.Declarations[1]);
		Assert.True(immutable.IsImmutable);
		Assert.Equal(2, immutable.PrimaryParameters.Count);

		var box = Assert.IsType<UnitDeclaration>(result.Tree.Declarations[2]);
		Assert.Equal(["T"], box.DeclaredGenericParameters.Select(parameter => parameter.Name));
		Assert.Single(box.PrimaryParameters);
		Assert.Equal("T", box.PrimaryParameters[0].Type?.ToString());
	}
}
