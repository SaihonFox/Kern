using Kern.Bytecode;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;

namespace Kern.Compiler.CodeGen;

/// <summary>Кодогенерация преобразований значений, включая <c>str</c> ↔ <c>list&lt;char&gt;</c>.</summary>
public sealed partial class CodeGenerator
{
	/// <summary>
	/// Конверсия по классифицированному виду (spec §3): <c>str</c> ↔ <c>list&lt;char&gt;</c>
	/// выполняются отдельными опкодами, остальные — числовыми <c>Conv*</c>.
	/// </summary>
	private void EmitConversion(Conversion conversion, KernType target)
	{
		switch (conversion.Kind)
		{
			case ConversionKind.StrToListChar:
				Emit(OpCode.StrChars);
				return;
			case ConversionKind.ListCharToStr:
				Emit(OpCode.CharsToStr);
				return;
		}

		EmitConversion(target);
	}

	private void EmitConversion(KernType target)
	{
		if (!TypeSigMap.TryPrimitive(target, out var primitive))
			return;

		switch (primitive)
		{
			case PrimitiveKind.I8: Emit(OpCode.ConvI8); break;
			case PrimitiveKind.I16: Emit(OpCode.ConvI16); break;
			case PrimitiveKind.I32: Emit(OpCode.ConvI32); break;
			case PrimitiveKind.I64: Emit(OpCode.ConvI64); break;
			case PrimitiveKind.U8: Emit(OpCode.ConvU8); break;
			case PrimitiveKind.U16: Emit(OpCode.ConvU16); break;
			case PrimitiveKind.U32: Emit(OpCode.ConvU32); break;
			case PrimitiveKind.U64: Emit(OpCode.ConvU64); break;
			case PrimitiveKind.F16: Emit(OpCode.ConvF16); break;
			case PrimitiveKind.F32: Emit(OpCode.ConvF32); break;
			case PrimitiveKind.F64: Emit(OpCode.ConvF64); break;
			case PrimitiveKind.Char: Emit(OpCode.ConvChar); break;
			case PrimitiveKind.Bool: Emit(OpCode.ConvBool); break;
		}
	}
}
