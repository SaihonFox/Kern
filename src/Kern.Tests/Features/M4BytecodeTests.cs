using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Bytecode;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Native;
using Kern.Compiler.Optimization;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;
using Xunit;
using CompilerBuiltin = Kern.Compiler.Semantics.Types.BuiltinGenericKind;
using CompilerBuiltinType = Kern.Compiler.Semantics.Types.BuiltinGenericType;

namespace Kern.Tests.Features;

/// <summary>
/// M4: свойства в <c>.kbc</c> (запись/чтение <c>PropertyDef</c> с атрибутами), свёртка
/// констант (<c>-O1</c>) и полнота подписей типов: union/функция/параметр типа и
/// канонические имена встроенных generic-типов, которые разрешает VM.
/// </summary>
public sealed class M4BytecodeTests
{
	[Fact]
	public void Property_defs_round_trip_through_kbc()
	{
		var compiled = Compile(PropertySource);
		var stored = Clone(compiled.Module);
		var type = Assert.Single(stored.TypePool, item => item.Name == "Box");

		Assert.Equal(4, type.Properties.Count);

		var count = Assert.Single(type.Properties, item => item.Name == "count");
		Assert.Equal(TypeSig.I32, count.Type);
		Assert.Equal(stored.TypePool.IndexOf(type), count.OwnerType);
		Assert.True(count.Getter >= 0);
		Assert.True(count.Setter >= 0);
		Assert.Equal(-1, count.Init);
		Assert.True(count.BackingField >= 0);
		Assert.False((count.Flags & MemberFlags.Readonly) != 0);

		var doubled = Assert.Single(type.Properties, item => item.Name == "doubled");
		Assert.True(doubled.Getter >= 0);
		Assert.Equal(-1, doubled.Setter);
		Assert.Equal(-1, doubled.Init);
		Assert.Equal(-1, doubled.BackingField);
		Assert.True((doubled.Flags & MemberFlags.Readonly) != 0);
		Assert.Empty(doubled.Attributes);
	}

	[Fact]
	public void Property_attributes_survive_round_trip()
	{
		var compiled = Compile(PropertySource);
		var stored = Clone(compiled.Module);
		var type = Assert.Single(stored.TypePool, item => item.Name == "Box");

		var marked = Assert.Single(type.Properties, item => item.Name == "marked");
		var cfg = Assert.Single(marked.Attributes);
		Assert.Equal("cfg", cfg.Name);
		Assert.Equal(KConst.Str("prop"), Assert.Single(cfg.Args));

		var flagged = Assert.Single(type.Properties, item => item.Name == "flagged");
		Assert.Equal("must_use", Assert.Single(flagged.Attributes).Name);

		// Атрибут свойства дублируется на его accessor'ах (рефлексия и линковка идут по функциям).
		var accessor = Assert.Single(
			stored.FunctionPool,
			item => item.Name == "flagged" && (item.Flags & MemberFlags.Getter) != 0);
		Assert.Equal("must_use", Assert.Single(accessor.Attributes).Name);
	}

	[Fact]
	public void Auto_property_and_expression_body_work_at_runtime()
	{
		var compiled = Compile(PropertySource + """

			fun value() -> i32 {
				Box box = Box()
				box.count + box.doubled
			}
			""");

		Assert.Equal(KValue.I32(9), Run(compiled.Module, "value"));
	}

	[Fact]
	public void Constant_folding_keeps_results_and_removes_constant_operations()
	{
		var compiled = Compile("""
			fun arithmetic() -> i32 { 2 + 3 * 4 }
			fun comparison() -> bool { 2 * 3 < 7 }
			fun bitwise() -> i32 { (6 & 3) | 8 }
			fun shift() -> i32 { 1 << 4 }
			fun unary() -> i32 { -(2 + 2) }
			fun not_true() -> bool { !true }
			fun concat() -> str { "ab" + "cd" }
			""");

		KbcModule before = Clone(compiled.Module);
		KbcModule after = ConstantFolder.Fold(Clone(compiled.Module));

		Assert.Equal(Run(before, "arithmetic"), Run(after, "arithmetic"));
		Assert.Equal(Run(before, "comparison"), Run(after, "comparison"));
		Assert.Equal(Run(before, "bitwise"), Run(after, "bitwise"));
		Assert.Equal(Run(before, "shift"), Run(after, "shift"));
		Assert.Equal(Run(before, "unary"), Run(after, "unary"));
		Assert.Equal(Run(before, "not_true"), Run(after, "not_true"));
		Assert.Equal(Run(before, "concat"), Run(after, "concat"));

		Assert.Equal(KValue.I32(14), Run(after, "arithmetic"));
		Assert.Equal(KValue.Bool(true), Run(after, "comparison"));
		Assert.Equal(KValue.I32(10), Run(after, "bitwise"));
		Assert.Equal(KValue.I32(16), Run(after, "shift"));
		Assert.Equal(KValue.I32(-4), Run(after, "unary"));
		Assert.Equal(KValue.Bool(false), Run(after, "not_true"));
		Assert.Equal(KValue.Str("abcd"), Run(after, "concat"));

		Assert.DoesNotContain(OpCode.Add, Ops(after, "arithmetic"));
		Assert.DoesNotContain(OpCode.Mul, Ops(after, "arithmetic"));
		Assert.DoesNotContain(OpCode.Mul, Ops(after, "comparison"));
		Assert.DoesNotContain(OpCode.Lt, Ops(after, "comparison"));
		Assert.DoesNotContain(OpCode.BitAnd, Ops(after, "bitwise"));
		Assert.DoesNotContain(OpCode.BitOr, Ops(after, "bitwise"));
		Assert.DoesNotContain(OpCode.Shl, Ops(after, "shift"));
		Assert.DoesNotContain(OpCode.Add, Ops(after, "unary"));
		Assert.DoesNotContain(OpCode.Neg, Ops(after, "unary"));
		Assert.DoesNotContain(OpCode.Not, Ops(after, "not_true"));
		Assert.DoesNotContain(OpCode.Add, Ops(after, "concat"));

		foreach (var name in new[] { "arithmetic", "comparison", "bitwise", "shift", "unary", "not_true", "concat" })
			Assert.True(
				Ops(after, name).Count < Ops(before, name).Count,
				$"в функции {name} свёртка не уменьшила код");
	}

	[Fact]
	public void Constant_folding_remaps_targets_of_folded_branches()
	{
		const string source = """
			fun branch(i32 n) -> i32 {
				if n > 0 { 1 + 2 } else { 3 + 4 }
			}
			""";

		var compiled = Compile(source);
		KbcModule before = Clone(compiled.Module);
		KbcModule after = ConstantFolder.Fold(Clone(compiled.Module));

		Assert.Equal(KValue.I32(3), Run(before, "branch", KValue.I32(1)));
		Assert.Equal(KValue.I32(7), Run(before, "branch", KValue.I32(0)));
		Assert.Equal(KValue.I32(3), Run(after, "branch", KValue.I32(1)));
		Assert.Equal(KValue.I32(7), Run(after, "branch", KValue.I32(0)));
		Assert.DoesNotContain(OpCode.Add, Ops(after, "branch"));
		Assert.True(Ops(after, "branch").Count < Ops(before, "branch").Count);
	}

	[Fact]
	public void Constant_folding_does_not_cross_a_jump_target()
	{
		var module = new KbcModule { Name = "jump" };
		module.AddFunction(new FunctionDef
		{
			Name = "guarded",
			ReturnType = TypeSig.I32,
			MaxStack = 2,
			Code =
			[
				(int)OpCode.Jmp, 4,
				(int)OpCode.PushI32, 1,
				(int)OpCode.PushI32, 2,
				(int)OpCode.Add,
				(int)OpCode.Ret,
			],
		});
		module.AddFunction(new FunctionDef
		{
			Name = "free",
			ReturnType = TypeSig.I32,
			MaxStack = 2,
			Code =
			[
				(int)OpCode.PushI32, 1,
				(int)OpCode.PushI32, 2,
				(int)OpCode.Add,
				(int)OpCode.Ret,
			],
		});

		int[] guarded = [.. module.FunctionPool[0].Code];
		ConstantFolder.Fold(module);

		// Цель перехода внутри литерала и операции: пара не сворачивается, код не меняется.
		Assert.Equal(guarded, module.FunctionPool[0].Code);
		// Контрольный случай без цели перехода сворачивается до одного литерала.
		Assert.Equal(
			new[] { (int)OpCode.PushI32, 3, (int)OpCode.Ret },
			module.FunctionPool[1].Code);
	}

	[Fact]
	public void Union_and_function_types_keep_their_structure_in_signatures()
	{
		const string source = """
			type Choice = [i32, str]

			fun pick(Choice value) -> Choice value
			fun apply(fun(i32) -> i32 f, i32 value) -> i32 f(value)
			""";

		var compiled = Compile(source);

		var pick = Assert.Single(compiled.Module.FunctionPool, item => item.Name == "pick");
		var union = Assert.IsType<TypeSig.UnionSig>(pick.ReturnType);
		Assert.Equal("[i32, str]", union.Canonical);

		var apply = Assert.Single(compiled.Module.FunctionPool, item => item.Name == "apply");
		var function = Assert.IsType<TypeSig.FuncSig>(apply.ParamTypes[0]);
		Assert.Equal(TypeSig.I32, Assert.Single(function.Params));
		Assert.Equal(TypeSig.I32, function.Return);

		// До исправления оба вида деградировали до `object` (подпись не совпадала с типом).
		Assert.Equal("fun(i32) -> i32", Signatures(compiled.Module, "apply")[0]);
	}

	[Fact]
	public void Union_and_function_types_are_reified_by_typeof()
	{
		var compiled = Compile("""
			type Choice = [i32, str]

			fun union_name() -> str { format(typeof<Choice>()) }
			fun function_name() -> str { format(typeof<fun(i32) -> i32>()) }
			""");

		Assert.Equal(KValue.Str("[i32, str]"), Run(compiled.Module, "union_name"));
		Assert.Equal(KValue.Str("fun(i32) -> i32"), Run(compiled.Module, "function_name"));
	}

	[Fact]
	public void Generic_parent_signature_keeps_the_type_parameter()
	{
		const string source = """
			type Base<T>()

			type Child<T>()[Base<T>]
			""";

		var compiled = Compile(source);
		var stored = Clone(compiled.Module);

		var child = Assert.Single(stored.TypePool, item => item.Name == "Child");
		var parent = Assert.IsType<TypeSig.NamedSig>(Assert.Single(child.Parents));
		Assert.Equal("main.Base", parent.FullName);
		var argument = Assert.IsType<TypeSig.ParamSig>(Assert.Single(parent.Args));
		Assert.Equal(0, argument.Scope);
		Assert.Equal(0, argument.Index);
		Assert.Equal("T", argument.Name);
	}

	[Fact]
	public void Generic_parent_substitution_reifies_the_type_argument()
	{
		var compiled = Compile("""
			type Base<T>()

			type Child<T>()[Base<T>]
			""");

		var child = Assert.Single(compiled.Module.TypePool, item => item.Name == "Child");
		var parentSig = Assert.IsType<TypeSig.NamedSig>(Assert.Single(child.Parents));
		var factory = new TypeFactory(compiled.Module);
		var childType = factory.Named(child, factory.Prim(PrimKind.I32));

		var parent = factory.FromSig(parentSig, childType);

		Assert.Equal("main.Base<i32>", parent.Canonical);
	}

	[Fact]
	public void Every_compiler_builtin_generic_name_resolves_in_the_vm()
	{
		var factory = new TypeFactory(new KbcModule { Name = "test" });

		foreach (CompilerBuiltin kind in Enum.GetValues<CompilerBuiltin>())
		{
			var name = CompilerBuiltinType.GetCanonicalName(kind);
			var arity = CompilerBuiltinType.GetArity(kind);
			TypeSig[] arguments = [.. Enumerable.Repeat(TypeSig.I32, arity)];

			var resolved = Assert.IsType<KBuiltinGenericType>(
				factory.FromSig(new TypeSig.NamedSig(name, arguments)));
			Assert.Equal($"{name}<{string.Join(", ", arguments.Select(_ => "i32"))}>", resolved.Canonical);
		}
	}

	[Fact]
	public void Set_signature_is_reified_by_typeof()
	{
		var compiled = Compile("""
			fun name() -> str { format(typeof<set<i32>>()) }
			""");

		Assert.Equal(KValue.Str("set<i32>"), Run(compiled.Module, "name"));
	}

	private const string PropertySource = """
		type Box {
			i32 count { get; set } = 3
			i32 doubled => count * 2
			#[cfg("prop")] i32 marked => 7
			#[must_use] i32 flagged { get; set } = 1
		}
		""";

	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	private static KValue Run(KbcModule module, string function, params KValue[] arguments) =>
		new Interpreter(module, new NativeResolver(Catalog())).Run(function, arguments);

	private static KbcModule Clone(KbcModule module) => KbcReader.Read(KbcWriter.Write(module));

	private static List<OpCode> Ops(KbcModule module, string function)
	{
		var definition = Assert.Single(module.FunctionPool, item => item.Name == function);
		return OpCodes(definition.Code);
	}

	private static List<string> Signatures(KbcModule module, string function)
	{
		var definition = Assert.Single(module.FunctionPool, item => item.Name == function);
		return [.. definition.ParamTypes.Select(type => type.Canonical)];
	}

	/// <summary>Линейный разбор кода функции (операнды пропускаются по <see cref="OpCodeInfo"/>).</summary>
	private static List<OpCode> OpCodes(int[] code)
	{
		var result = new List<OpCode>();
		for (var offset = 0; offset < code.Length;)
		{
			var op = (OpCode)code[offset];
			result.Add(op);
			offset += 1 + OpCodeInfo.OperandCount(op);
		}

		return result;
	}

	private static CodegenResult Compile(string source)
	{
		var outcome = CompileWithDiagnostics(source);
		Assert.DoesNotContain(outcome.Diagnostics, diagnostic => diagnostic.IsError);
		Assert.NotNull(outcome.Code);
		return outcome.Code!;
	}

	private sealed record Outcome(CodegenResult? Code, IReadOnlyList<Diagnostic> Diagnostics);

	/// <summary>Полный конвейер исходник → байткод без запуска VM.</summary>
	private static Outcome CompileWithDiagnostics(string source)
	{
		var diagnostics = new List<Diagnostic>();
		var parsed = Parser.Parse(source);
		diagnostics.AddRange(parsed.Diagnostics);
		if (HasErrors(diagnostics))
			return new Outcome(null, diagnostics);

		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		diagnostics.AddRange(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		diagnostics.AddRange(collectorDiagnostics);

		var catalog = Catalog();
		ModuleSymbol imported = NativeSymbolBuilder.Import(module, catalog, out var importProblems);
		Assert.Empty(importProblems);

		var bound = new Binder(imported, table).Bind();
		diagnostics.AddRange(bound.Diagnostics);
		if (HasErrors(diagnostics))
			return new Outcome(null, diagnostics);

		var generated = new CodeGenerator().Generate(bound);
		diagnostics.AddRange(generated.Diagnostics);
		return HasErrors(diagnostics)
			? new Outcome(null, diagnostics)
			: new Outcome(generated, diagnostics);
	}

	private static bool HasErrors(IReadOnlyList<Diagnostic> diagnostics) =>
		diagnostics.Any(diagnostic => diagnostic.IsError);
}
