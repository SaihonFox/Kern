using Kern.Bytecode;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Execution;

/// <summary>
/// B1: побитовые операторы на реальном исходнике Kern — codegen → VM.
/// Сдвиг маскирует счётчик по ширине левого операнда (31/63), `&gt;&gt;` беззнаковых — логический.
/// </summary>
public sealed class BitwiseTests
{
	[Fact]
	public void And_or_xor_on_i32()
	{
		Assert.Equal(KValue.I32(0), Run("fun f() -> i32 { 0xF0 & 0x0F }", "f"));
		Assert.Equal(KValue.I32(255), Run("fun f() -> i32 { 0xF0 | 0x0F }", "f"));
		Assert.Equal(KValue.I32(12), Run("fun f() -> i32 { 0b1010 ^ 0b0110 }", "f"));
	}

	[Fact]
	public void Shifts_and_bitwise_not_on_i32()
	{
		Assert.Equal(KValue.I32(16), Run("fun f() -> i32 { 1 << 4 }", "f"));
		// Счётчик маскируется по ширине i32: 32 & 31 == 0.
		Assert.Equal(KValue.I32(1), Run("fun f() -> i32 { 1 << 32 }", "f"));
		Assert.Equal(KValue.I32(int.MinValue), Run("fun f() -> i32 { 1 << 31 }", "f"));
		Assert.Equal(KValue.I32(-4), Run("fun f() -> i32 { -8 >> 1 }", "f"));
		Assert.Equal(KValue.I32(2), Run("fun f() -> i32 { 8 >> 2 }", "f"));
		Assert.Equal(KValue.I32(-1), Run("fun f() -> i32 { ~0 }", "f"));
	}

	[Fact]
	public void U64_bitwise_not_and_logical_shift()
	{
		KValue not = Run("fun f() -> u64 { ~0u64 }", "f");
		Assert.Equal(ValueKind.U64, not.Kind);
		Assert.Equal(ulong.MaxValue, not.AsU64);

		KValue shifted = Run("fun f() -> u64 { ~0u64 >> 4 }", "f");
		Assert.Equal(ValueKind.U64, shifted.Kind);
		Assert.Equal(1152921504606846975UL, shifted.AsU64);
	}

	[Fact]
	public void U64_max_literal_compiles_and_shifts_logically()
	{
		KValue value = Run("fun f() -> u64 { 0xFFFF_FFFF_FFFF_FFFF }", "f");
		Assert.Equal(ValueKind.U64, value.Kind);
		Assert.Equal(18446744073709551615UL, value.AsU64);

		KValue shifted = Run("fun f() -> u64 { 0xFFFF_FFFF_FFFF_FFFF >> 4 }", "f");
		Assert.Equal(1152921504606846975UL, shifted.AsU64);
	}

	[Fact]
	public void U64_literal_survives_kbc_round_trip()
	{
		var result = Compile("fun f() -> u64 { 0xFFFF_FFFF_FFFF_FFFF >> 4 }");
		KbcModule restored = KbcReader.Read(KbcWriter.Write(result.Module));

		KValue value = new Interpreter(restored).Run("f", []);
		Assert.Equal(ValueKind.U64, value.Kind);
		Assert.Equal(1152921504606846975UL, value.AsU64);
	}

	[Fact]
	public void Shift_count_may_have_a_different_integer_kind()
	{
		KValue value = Run(
			"""
			fun f() -> u8 {
				u8 b = 1u8
				i32 n = 3
				b << n
			}
			""",
			"f");

		Assert.Equal(ValueKind.U8, value.Kind);
		Assert.Equal((byte)8, value.AsU8);
	}

	[Fact]
	public void Compound_bitwise_assignments()
	{
		Assert.Equal(KValue.I32(8), Run(
			"""
			fun f() -> i32 {
				i32 flags = 0
				flags |= 1 << 3
				flags
			}
			""",
			"f"));

		Assert.Equal(KValue.I32(8), Run(
			"""
			fun f() -> i32 {
				i32 flags = 0b1000
				flags &= 0b1010
				flags
			}
			""",
			"f"));

		Assert.Equal(KValue.I32(0), Run(
			"""
			fun f() -> i32 {
				i32 flags = 0b1000
				flags ^= 0b1000
				flags
			}
			""",
			"f"));
	}

	[Fact]
	public void Compound_shift_assignments()
	{
		KValue value = Run(
			"""
			fun f() -> i32 {
				i32 n = 16
				n <<= 1
				n >>= 3
				n
			}
			""",
			"f");

		Assert.Equal(KValue.I32(4), value);
	}

	[Fact]
	public void Fnv_style_hash_loop_wraps_like_i32()
	{
		// Независимый расчёт (FNV-1a, seed 7, byte = 65 + i*7, prime 16777619, wrap i32) = -491960921.
		KValue value = Run(
			"""
			fun f() -> i32 {
				i32 hash = 7
				i32 i = 0
				loop i < 16 {
					i32 value = 65 + i * 7
					hash = hash ^ value
					hash = hash * 16777619
					i += 1
				}
				hash
			}
			""",
			"f");

		Assert.Equal(KValue.I32(-491960921), value);
	}

	private static KValue Run(string source, string function, params KValue[] arguments)
	{
		var result = Compile(source);
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
		var generated = new CodeGenerator().Generate(bound);
		Assert.DoesNotContain(generated.Diagnostics, diagnostic => diagnostic.IsError);
		return generated;
	}
}
