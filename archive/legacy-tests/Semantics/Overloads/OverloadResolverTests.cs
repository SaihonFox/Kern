using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Overloads;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Semantics.Overloads;

public sealed class OverloadResolverTests
{
	[Fact]
	public void Identity_beats_widening()
	{
		var exact = Fn("f", PrimitiveType.I32, Param("x", PrimitiveType.I32));
		var wide = Fn("f", PrimitiveType.I32, Param("x", PrimitiveType.I64));

		var resolution = Resolve([wide, exact], Arg(PrimitiveType.I32));

		Assert.True(resolution.Success);
		Assert.Same(exact, resolution.Function);
		Assert.Equal(ConversionKind.Identity, resolution.Arguments[0].Conversion.Kind);
	}

	[Fact]
	public void Only_widening_candidate_applies()
	{
		var wide = Fn("f", PrimitiveType.I32, Param("x", PrimitiveType.I64));

		var resolution = Resolve([wide], Arg(PrimitiveType.I32));

		Assert.True(resolution.Success);
		Assert.Equal(ConversionKind.NumericWidening, resolution.Arguments[0].Conversion.Kind);
	}

	[Fact]
	public void Constant_literal_prefers_signed_target()
	{
		var signed = Fn("f", PrimitiveType.Void, Param("x", PrimitiveType.I8));
		var unsigned = Fn("f", PrimitiveType.Void, Param("x", PrimitiveType.U8));
		Assert.True(NumericLiteralValue.TryParse("5", out var five));

		var resolution = Resolve([unsigned, signed], Arg(PrimitiveType.I32, five));

		Assert.True(resolution.Success);
		Assert.Same(signed, resolution.Function);
	}

	[Fact]
	public void Constant_that_does_not_fit_is_not_applicable()
	{
		var small = Fn("f", PrimitiveType.Void, Param("x", PrimitiveType.U8));
		Assert.True(NumericLiteralValue.TryParse("300", out var threeHundred));

		var resolution = Resolve([small], Arg(PrimitiveType.I32, threeHundred));

		Assert.False(resolution.Success);
		Assert.Equal("KRN1401", Assert.Single(resolution.Diagnostics).Code);
	}

	[Fact]
	public void No_candidate_reports_KRN1401()
	{
		var candidate = Fn("f", PrimitiveType.Void, Param("x", PrimitiveType.I32));

		var resolution = Resolve([candidate], Arg(PrimitiveType.Str));

		Assert.False(resolution.Success);
		var diagnostic = Assert.Single(resolution.Diagnostics);
		Assert.Equal("KRN1401", diagnostic.Code);
		Assert.Contains("f", diagnostic.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Wrong_arity_reports_KRN1401()
	{
		var candidate = Fn("f", PrimitiveType.Void, Param("x", PrimitiveType.I32));

		var resolution = OverloadResolver.Resolve([candidate], new CallArguments([], name: "f"));

		Assert.Equal("KRN1401", Assert.Single(resolution.Diagnostics).Code);
	}

	[Fact]
	public void Duplicate_signatures_are_ambiguous()
	{
		var first = Fn("f", PrimitiveType.I32, Param("x", PrimitiveType.I32));
		var second = Fn("f", PrimitiveType.I32, Param("y", PrimitiveType.I32));

		var resolution = Resolve([first, second], Arg(PrimitiveType.I32));

		Assert.False(resolution.Success);
		Assert.Equal("KRN1402", Assert.Single(resolution.Diagnostics).Code);
	}

	[Fact]
	public void Return_type_does_not_participate_in_selection()
	{
		var asInt = Fn("f", PrimitiveType.I32);
		var asStr = Fn("f", PrimitiveType.Str);

		var resolution = OverloadResolver.Resolve([asInt, asStr], new CallArguments([], name: "f"));

		Assert.Equal("KRN1402", Assert.Single(resolution.Diagnostics).Code);
	}

	[Fact]
	public void Named_arguments_map_to_parameters()
	{
		var candidate = Fn("f", PrimitiveType.Void,
			Param("a", PrimitiveType.I32),
			Param("b", PrimitiveType.Str));

		var resolution = OverloadResolver.Resolve([candidate], new CallArguments(
			[CallArgument.Named("b", PrimitiveType.Str), CallArgument.Named("a", PrimitiveType.I32)],
			name: "f"));

		Assert.True(resolution.Success);
		Assert.Equal(0, resolution.Arguments[0].ParameterIndex);
		Assert.Equal(1, resolution.Arguments[1].ParameterIndex);
	}

	[Fact]
	public void Positional_argument_after_named_is_rejected()
	{
		var candidate = Fn("f", PrimitiveType.Void,
			Param("a", PrimitiveType.I32),
			Param("b", PrimitiveType.I32));

		var resolution = OverloadResolver.Resolve([candidate], new CallArguments(
			[CallArgument.Named("b", PrimitiveType.I32), Arg(PrimitiveType.I32)],
			name: "f"));

		Assert.Equal("KRN1401", Assert.Single(resolution.Diagnostics).Code);
	}

	[Fact]
	public void Unknown_named_argument_is_rejected()
	{
		var candidate = Fn("f", PrimitiveType.Void, Param("a", PrimitiveType.I32));

		var resolution = OverloadResolver.Resolve([candidate], new CallArguments(
			[CallArgument.Named("b", PrimitiveType.I32)],
			name: "f"));

		Assert.Equal("KRN1401", Assert.Single(resolution.Diagnostics).Code);
	}

	[Fact]
	public void Default_argument_is_recorded_and_default_count_breaks_ties()
	{
		var withoutDefault = Fn("f", PrimitiveType.Void, Param("a", PrimitiveType.I32));
		var withDefault = Fn("f", PrimitiveType.Void,
			Param("a", PrimitiveType.I32),
			Param("b", PrimitiveType.Str, hasDefault: true));

		var resolution = Resolve([withDefault, withoutDefault], Arg(PrimitiveType.I32));

		Assert.True(resolution.Success);
		Assert.Same(withoutDefault, resolution.Function);

		var defaulted = Resolve([withDefault], Arg(PrimitiveType.I32));
		Assert.True(defaulted.Success);
		Assert.Equal(2, defaulted.Arguments.Count);
		Assert.Null(defaulted.Arguments[1].Argument);
	}

	[Fact]
	public void Generic_type_is_inferred_from_argument_and_substituted_in_result()
	{
		var typeParameter = new TypeParameterType("T", []);
		var generic = Generic("id", typeParameter, typeParameter, Param("x", typeParameter));

		var resolution = Resolve([generic], Arg(PrimitiveType.I32));

		Assert.True(resolution.Success);
		Assert.Equal(PrimitiveType.I32, Assert.Single(resolution.TypeArguments));
		Assert.Equal(PrimitiveType.I32, resolution.ReturnType);
	}

	[Fact]
	public void Multiple_bounds_choose_the_numeric_common_type()
	{
		var typeParameter = new TypeParameterType("T", []);
		var generic = Generic("f", typeParameter, PrimitiveType.Void,
			Param("a", typeParameter),
			Param("b", typeParameter));

		var resolution = OverloadResolver.Resolve([generic], new CallArguments(
			[Arg(PrimitiveType.I32), Arg(PrimitiveType.I64)],
			name: "f"));

		Assert.True(resolution.Success);
		Assert.Equal(PrimitiveType.I64, Assert.Single(resolution.TypeArguments));
	}

	[Fact]
	public void Explicit_type_arguments_are_used_as_is()
	{
		var typeParameter = new TypeParameterType("T", []);
		var generic = Generic("id", typeParameter, typeParameter, Param("x", typeParameter));

		var resolution = OverloadResolver.Resolve([generic], new CallArguments(
			[Arg(PrimitiveType.Str)],
			explicitTypeArguments: [PrimitiveType.Str],
			name: "id"));

		Assert.True(resolution.Success);
		Assert.Equal(PrimitiveType.Str, Assert.Single(resolution.TypeArguments));
	}

	[Fact]
	public void Explicit_type_argument_arity_must_match()
	{
		var typeParameter = new TypeParameterType("T", []);
		var generic = Generic("id", typeParameter, typeParameter, Param("x", typeParameter));

		var resolution = OverloadResolver.Resolve([generic], new CallArguments(
			[Arg(PrimitiveType.Str)],
			explicitTypeArguments: [PrimitiveType.Str, PrimitiveType.I32],
			name: "id"));

		Assert.Equal("KRN1401", Assert.Single(resolution.Diagnostics).Code);
	}

	[Fact]
	public void Generic_constraint_is_checked()
	{
		var typeParameter = new TypeParameterType("T", [PrimitiveType.Inum]);
		var generic = Generic("f", typeParameter, PrimitiveType.Void, Param("x", typeParameter));

		Assert.True(Resolve([generic], Arg(PrimitiveType.I32)).Success);
		Assert.Equal("KRN1401", Assert.Single(Resolve([generic], Arg(PrimitiveType.Str)).Diagnostics).Code);
	}

	[Fact]
	public void Non_generic_candidate_wins_a_tie()
	{
		var typeParameter = new TypeParameterType("T", []);
		var generic = Generic("f", typeParameter, PrimitiveType.Void, Param("x", typeParameter));
		var concrete = Fn("f", PrimitiveType.Void, Param("x", PrimitiveType.I32));

		var resolution = Resolve([generic, concrete], Arg(PrimitiveType.I32));

		Assert.True(resolution.Success);
		Assert.Same(concrete, resolution.Function);
	}

	[Fact]
	public void Vararg_normal_form_takes_a_list()
	{
		var candidate = Fn("f", PrimitiveType.Void,
			Param("rest", ListOf(PrimitiveType.Str), vararg: true));

		var resolution = Resolve([candidate], Arg(ListOf(PrimitiveType.Str)));

		Assert.True(resolution.Success);
		Assert.False(Assert.Single(resolution.Arguments).IsExpanded);
	}

	[Fact]
	public void Vararg_expanded_form_is_retried_after_conversion_failure()
	{
		var candidate = Fn("f", PrimitiveType.Void,
			Param("a", PrimitiveType.I32),
			Param("rest", ListOf(PrimitiveType.Str), vararg: true));

		var resolution = OverloadResolver.Resolve([candidate], new CallArguments(
			[Arg(PrimitiveType.I32), Arg(PrimitiveType.Str)],
			name: "f"));

		Assert.True(resolution.Success);
		Assert.Equal(2, resolution.Arguments.Count);
		Assert.False(resolution.Arguments[0].IsExpanded);
		Assert.True(resolution.Arguments[1].IsExpanded);
		Assert.Equal(1, resolution.Arguments[1].ParameterIndex);
	}

	[Fact]
	public void Named_argument_can_pass_the_vararg_list()
	{
		var candidate = Fn("f", PrimitiveType.Void,
			Param("rest", ListOf(PrimitiveType.Str), vararg: true));

		var resolution = OverloadResolver.Resolve([candidate], new CallArguments(
			[CallArgument.Named("rest", ListOf(PrimitiveType.Str))],
			name: "f"));

		Assert.True(resolution.Success);
		Assert.False(Assert.Single(resolution.Arguments).IsExpanded);
	}

	[Fact]
	public void Extension_receiver_must_be_provided_and_is_converted()
	{
		var extension = new FunctionSymbol("shout", FunctionKind.Function)
		{
			ReceiverType = PrimitiveType.Str,
			ReturnType = PrimitiveType.Str,
			Parameters = []
		};

		var withReceiver = OverloadResolver.Resolve([extension], new CallArguments(
			[],
			receiverType: PrimitiveType.Str,
			name: "shout"));
		Assert.True(withReceiver.Success);
		Assert.NotNull(withReceiver.ReceiverConversion);

		var withoutReceiver = OverloadResolver.Resolve([extension], new CallArguments([], name: "shout"));
		Assert.Equal("KRN1401", Assert.Single(withoutReceiver.Diagnostics).Code);
	}

	[Fact]
	public void Generic_extension_infers_element_type_from_receiver()
	{
		var typeParameter = new TypeParameterType("T", []);
		var extension = new FunctionSymbol("first", FunctionKind.Function)
		{
			GenericParameters = [typeParameter],
			ReceiverType = new BuiltinGenericType(BuiltinGenericKind.List, [typeParameter]),
			ReturnType = typeParameter,
			Parameters = []
		};

		var resolution = OverloadResolver.Resolve([extension], new CallArguments(
			[],
			receiverType: ListOf(PrimitiveType.I32),
			name: "first"));

		Assert.True(resolution.Success);
		Assert.Equal(PrimitiveType.I32, Assert.Single(resolution.TypeArguments));
		Assert.Equal(PrimitiveType.I32, resolution.ReturnType);
	}

	[Fact]
	public void Derived_owner_hides_base_overload_on_a_tie()
	{
		var baseType = new TypeSymbol { Name = "Base", Kind = SymbolKind.Class };
		var derivedType = new TypeSymbol
		{
			Name = "Derived",
			Kind = SymbolKind.Class,
			Bases = [new ClassType(baseType, [])]
		};
		var inBase = Fn("f", PrimitiveType.Void, Param("x", PrimitiveType.I32));
		var inDerived = Fn("f", PrimitiveType.Void, Param("x", PrimitiveType.I32));
		var baseFunction = WithOwner(inBase, baseType);
		var derivedFunction = WithOwner(inDerived, derivedType);

		var resolution = Resolve([baseFunction, derivedFunction], Arg(PrimitiveType.I32));

		Assert.True(resolution.Success);
		Assert.Same(derivedType, resolution.Function!.Owner);
	}

	[Fact]
	public void Error_argument_does_not_produce_diagnostics()
	{
		var candidate = Fn("f", PrimitiveType.Void, Param("x", PrimitiveType.I32));

		var resolution = Resolve([candidate], Arg(ErrorType.Instance));

		Assert.True(resolution.Success);
		Assert.Empty(resolution.Diagnostics);
		Assert.Same(candidate, resolution.Function);
	}

	[Fact]
	public void Lifted_nullable_target_applies_but_reverse_does_not()
	{
		var lifted = Fn("f", PrimitiveType.Void, Param("x", NullableType.Create(PrimitiveType.I64)));

		var resolution = Resolve([lifted], Arg(PrimitiveType.I32));
		Assert.True(resolution.Success);
		Assert.Equal(ConversionKind.Lifted, resolution.Arguments[0].Conversion.Kind);

		var narrowed = Fn("f", PrimitiveType.Void, Param("x", NullableType.Create(PrimitiveType.I32)));
		Assert.Equal("KRN1401", Assert.Single(Resolve([narrowed], Arg(PrimitiveType.I64)).Diagnostics).Code);
	}

	[Fact]
	public void Union_target_accepts_a_member()
	{
		var union = UnionType.Create([PrimitiveType.I32, PrimitiveType.Str]);
		var candidate = Fn("f", PrimitiveType.Void, Param("x", union));

		var resolution = Resolve([candidate], Arg(PrimitiveType.I32));

		Assert.True(resolution.Success);
		Assert.Equal(ConversionKind.UnionTarget, resolution.Arguments[0].Conversion.Kind);
	}

	[Fact]
	public void Defaulted_parameter_does_not_break_inference()
	{
		var typeParameter = new TypeParameterType("T", []);
		var generic = Generic("f", typeParameter, PrimitiveType.Void,
			Param("a", typeParameter),
			Param("b", typeParameter, hasDefault: true));

		var resolution = Resolve([generic], Arg(PrimitiveType.I32));

		Assert.True(resolution.Success);
		Assert.Equal(PrimitiveType.I32, Assert.Single(resolution.TypeArguments));
		Assert.Null(resolution.Arguments[1].Argument);
	}

	private static OverloadResolution Resolve(
		IReadOnlyList<FunctionSymbol> candidates,
		CallArgument argument) =>
		OverloadResolver.Resolve(candidates, new CallArguments([argument], name: "f"));

	private static CallArgument Arg(KernType type, NumericLiteralValue? constant = null) =>
		CallArgument.Positional(type, constant);

	private static ParameterSymbol Param(string name, KernType type, bool hasDefault = false, bool vararg = false) =>
		new(name, type, hasDefault ? new IdentifierExpression("default", default) : null, false, vararg, default);

	private static FunctionSymbol Fn(string name, KernType returnType, params ParameterSymbol[] parameters) =>
		new(name, FunctionKind.Function)
		{
			Parameters = parameters,
			ReturnType = returnType
		};

	private static FunctionSymbol Generic(
		string name,
		TypeParameterType typeParameter,
		KernType returnType,
		params ParameterSymbol[] parameters) =>
		new(name, FunctionKind.Function)
		{
			GenericParameters = [typeParameter],
			Parameters = parameters,
			ReturnType = returnType
		};

	private static FunctionSymbol WithOwner(FunctionSymbol function, TypeSymbol owner) =>
		new(function.Name, function.Kind)
		{
			Parameters = function.Parameters,
			ReturnType = function.ReturnType,
			GenericParameters = function.GenericParameters,
			Owner = owner
		};

	private static BuiltinGenericType ListOf(KernType element) =>
		new(BuiltinGenericKind.List, [element]);
}
