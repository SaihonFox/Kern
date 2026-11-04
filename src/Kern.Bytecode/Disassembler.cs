using System.Globalization;
using System.Text;

namespace Kern.Bytecode;

/// <summary>
/// Текстовый дизассемблер .kbc для тестов и <c>kernc dis</c>. Вывод детерминирован,
/// перевод строки — всегда <c>\n</c>; операнды печатаются в порядке раскладки из <see cref="OpCodeInfo"/>.
/// </summary>
public static class Disassembler
{
	public static string Disassemble(KbcModule module)
	{
		ArgumentNullException.ThrowIfNull(module);
		StringBuilder sb = new();
		Line(sb, $"; KBC2 версия {KbcModule.Version}");
		Line(sb, $".module {KbcText.Escape(module.Name)}");

		Line(sb, $".refs {module.References.Count}");
		for (int i = 0; i < module.References.Count; i++)
			Line(sb, $"  [{i}] {KbcText.Escape(module.References[i])}");

		Line(sb, $".strings {module.StringPool.Count}");
		for (int i = 0; i < module.StringPool.Count; i++)
			Line(sb, $"  [{i}] {KbcText.Escape(module.StringPool[i])}");

		Line(sb, $".consts {module.ConstPool.Count}");
		for (int i = 0; i < module.ConstPool.Count; i++)
			Line(sb, $"  [{i}] {module.ConstPool[i].Describe()}");

		Line(sb, $".typeSigs {module.TypeSigPool.Count}");
		for (int i = 0; i < module.TypeSigPool.Count; i++)
			Line(sb, $"  [{i}] {module.TypeSigPool[i].Canonical}");

		Line(sb, $".typeSigLists {module.TypeSigListPool.Count}");
		for (int i = 0; i < module.TypeSigListPool.Count; i++)
			Line(sb, $"  [{i}] [{string.Join(", ", module.TypeSigListPool[i])}]");

		WriteTypes(sb, module);
		WriteFields(sb, module);
		WriteProperties(sb, module);
		WriteFunctions(sb, module);

		Line(sb, $".entry {module.EntryPoint}");
		Line(sb, $".static-init {module.StaticInit}");
		Line(sb, $".attrs {module.Attributes.Count}");
		foreach (AttributeUse attribute in module.Attributes) Line(sb, $"  {attribute}");
		return sb.ToString();
	}

	private static void WriteTypes(StringBuilder sb, KbcModule module)
	{
		Line(sb, $".types {module.TypePool.Count}");
		for (int i = 0; i < module.TypePool.Count; i++)
		{
			TypeDef type = module.TypePool[i];
			Line(sb,
				$"  [{i}] {type.Kind.ToString().ToLowerInvariant()} {KbcText.Escape(type.Name)} full={KbcText.Escape(type.FullName)}"
				+ $" generic={type.GenericParams.Count} parents={type.Parents.Count} fields={type.Fields.Count}"
				+ $" props={type.Properties.Count} methods={type.Methods.Count} ctors={type.Ctors.Count}"
				+ $" union={type.UnionMembers.Count} flags={type.Flags}"
				+ $" attrs={type.Attributes.Count}");
			foreach (string generic in type.GenericParams) Line(sb, $"    generic {KbcText.Escape(generic)}");
			foreach (TypeSig parent in type.Parents) Line(sb, $"    parent {parent.Canonical}");
			foreach (FieldDef field in type.Fields) Line(sb, $"    field {DescribeField(field)}");
			foreach (PropertyDef property in type.Properties) Line(sb, $"    prop {DescribeProperty(property)}");
			foreach (TypeSig member in type.UnionMembers) Line(sb, $"    union {member.Canonical}");
			WriteAttributes(sb, type.Attributes);
		}
	}

	private static void WriteFields(StringBuilder sb, KbcModule module)
	{
		Line(sb, $".fields {module.FieldPool.Count}");
		for (int i = 0; i < module.FieldPool.Count; i++)
			Line(sb, $"  [{i}] {DescribeField(module.FieldPool[i])}");
	}

	private static void WriteProperties(StringBuilder sb, KbcModule module)
	{
		Line(sb, $".properties {module.PropertyPool.Count}");
		for (int i = 0; i < module.PropertyPool.Count; i++)
			Line(sb, $"  [{i}] {DescribeProperty(module.PropertyPool[i])}");
	}

	private static string DescribeField(FieldDef field) =>
		$"{KbcText.Escape(field.Name)}: {field.Type.Canonical} slot={field.Slot} flags={field.Flags} attrs={field.Attributes.Count}";

	private static string DescribeProperty(PropertyDef property) =>
		$"{KbcText.Escape(property.Name)}: {property.Type.Canonical} owner={property.OwnerType}"
		+ $" get={property.Getter} set={property.Setter}"
		+ $" init={property.Init} backing={property.BackingField} flags={property.Flags} attrs={property.Attributes.Count}";

	private static void WriteFunctions(StringBuilder sb, KbcModule module)
	{
		Line(sb, $".functions {module.FunctionPool.Count}");
		for (int i = 0; i < module.FunctionPool.Count; i++)
		{
			FunctionDef function = module.FunctionPool[i];
			string parameters = function.ParamCount == 0
				? "params=0"
				: $"params={function.ParamCount} [{string.Join(", ", function.ParamTypes.Select(p => p.Canonical))}]";
			Line(sb,
				$"  #{i} {KbcText.Escape(function.Name)} {parameters} locals={function.LocalCount}"
				+ $" stack={function.MaxStack} owner={function.OwnerType} -> {function.ReturnType.Canonical}"
				+ $" flags={function.Flags}");
			if (function.SourceFile is not null)
				Line(sb, $"    source {KbcText.Escape(function.SourceFile)}");
			WriteAttributes(sb, function.Attributes);
			WriteCode(sb, module, function);
			foreach (LineEntry line in function.LineTable) Line(sb, $"    line {line.Line} @ {line.Offset}");
		}
	}

	private static void WriteAttributes(StringBuilder sb, IReadOnlyList<AttributeUse> attributes)
	{
		foreach (AttributeUse attribute in attributes) Line(sb, $"    attr {attribute}");
	}

	private static void WriteCode(StringBuilder sb, KbcModule module, FunctionDef function)
	{
		int[] code = function.Code;
		int pc = 0;
		while (pc < code.Length)
		{
			int start = pc;
			int raw = code[pc];
			if (!OpCodeInfo.IsDefined(raw))
			{
				Line(sb, $"    {Offset(start)}  <неизвестный опкод {raw}>");
				return;
			}
			OpCode op = (OpCode)raw;
			int operandCount = OpCodeInfo.OperandCount(op);
			if (pc + operandCount >= code.Length && operandCount > 0)
			{
				Line(sb, $"    {Offset(start)}  {op} <обрезано>");
				return;
			}
			pc++;
			int[] operands = new int[operandCount];
			for (int i = 0; i < operandCount; i++) operands[i] = code[pc + i];
			pc += operandCount;

			string text = operands.Length == 0 ? op.ToString() : $"{op} {string.Join(" ", operands)}";
			string comment = DescribeOperands(module, op, operands);
			Line(sb, $"    {Offset(start)}  {text}{(comment.Length == 0 ? "" : $"  ; {comment}")}");
		}
	}

	private static string DescribeOperands(KbcModule module, OpCode op, int[] operands)
	{
		switch (op)
		{
			case OpCode.PushConst when (uint)operands[0] < (uint)module.ConstPool.Count:
				return module.ConstPool[operands[0]].Describe();
			case OpCode.PushI64:
				return $"= {BitsToInt64(operands).ToString(CultureInfo.InvariantCulture)}";
			case OpCode.PushF64:
				return $"= {BitConverter.Int64BitsToDouble(BitsToInt64(operands)).ToString("R", CultureInfo.InvariantCulture)}";
			case OpCode.Call or OpCode.CallVirtual or OpCode.CallGeneric when (uint)operands[0] < (uint)module.FunctionPool.Count:
				return $"-> {KbcText.Escape(module.FunctionPool[operands[0]].Name)}";
			case OpCode.Slice:
				return operands[0] == 1 ? "конец включён" : "конец не включён";
			default:
				return string.Empty;
		}
	}

	private static long BitsToInt64(int[] operands) =>
		unchecked((long)(((ulong)(uint)operands[1] << 32) | (uint)operands[0]));

	private static string Offset(int value) => value.ToString("D4", CultureInfo.InvariantCulture);

	private static void Line(StringBuilder sb, string text) => sb.Append(text).Append('\n');
}
