using System.Globalization;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Types;
using Kern.Lib.Transpile.Core;
using Kern.Lib.Transpile.Mapping;

namespace Kern.Lib.Transpile.Targets.Kotlin;

/// <summary>Синтаксис Kotlin (языковые хуки; объявления — в <c>KotlinLowerer.Decls.cs</c>).</summary>
public sealed partial class KotlinLowerer(IMappings maps) : Lowerer(maps)
{
	public override string Id => "kotlin";
	public override string Extension => ".kt";
	protected override string End => "";
	protected override bool AlwaysStatement => true;
	protected override string DiscardText(string value) => value;
	protected override string RuntimeFileName => "KernRuntime.kt";
	protected override string RuntimeText => KotlinRuntime.Text;

	protected override string FileFor(BoundModule module) =>
		Naming.Pascal(module.Symbol.Name.Replace('.', '_')) + ".kt";

	private string PackageOf(BoundModule module) =>
		module.Symbol.Name.ToLowerInvariant().Replace('-', '_');

	// ------------------------------------------------------------------ имена

	protected override string LocalName(string kernName)
	{
		string name = Naming.Camel(kernName);
		return Naming.KotlinKeywords.Contains(name) ? $"`{name}`" : name;
	}

	protected override string MemberName(string kernName)
	{
		string name = Naming.Camel(kernName);
		return Naming.KotlinKeywords.Contains(name) ? $"`{name}`" : name;
	}

	protected override string TypeName(KernType type) => type switch
	{
		PrimitiveType primitive => Primitive(primitive),
		NilType => "Nothing?",
		ErrorType => "Any",
		NullableType nullable => TypeName(nullable.Underlying) + "?",
		ClassType @class => ClassName(@class.Symbol.Name, @class.Arguments),
		UnitType unit when ResultTypes.IsResultVariant(unit.Symbol) => "Any",
		UnitType unit => ClassName(unit.Symbol.Name, unit.Arguments),
		TypeParameterType parameter => Naming.Sanitize(parameter.Name),
		FunctionType function => FunctionName(function),
		TupleType tuple => TupleName(tuple),
		UnionType union => UnionName(union),
		BuiltinGenericType builtin => BuiltinName(builtin),
		_ => "Any"
	};

	private string ClassName(string name, IReadOnlyList<KernType> arguments)
	{
		string text = Naming.Pascal(name);
		if (arguments.Count > 0)
			text += "<" + string.Join(", ", arguments.Select(TypeName)) + ">";
		return text;
	}

	private string UnionName(UnionType union)
	{
		(KernType ok, KernType error) = ResultArguments(union);
		return $"KResult<{TypeName(ok)}, {TypeName(error)}>";
	}

	private string BuiltinName(BuiltinGenericType builtin)
	{
		if (builtin.BuiltinKind == BuiltinGenericKind.Result && builtin.Arguments.Count == 2)
			return $"KResult<{TypeName(builtin.Arguments[0])}, {TypeName(builtin.Arguments[1])}>";
		if (builtin.Arguments.Count == 0)
			return Naming.Pascal(BuiltinGenericType.GetCanonicalName(builtin.BuiltinKind));
		return builtin.BuiltinKind switch
		{
			BuiltinGenericKind.List => $"MutableList<{TypeName(builtin.Arguments[0])}>",
			BuiltinGenericKind.Array => $"Array<{TypeName(builtin.Arguments[0])}>",
			BuiltinGenericKind.Dict => $"MutableMap<{TypeName(builtin.Arguments[0])}, {TypeName(builtin.Arguments[1])}>",
			BuiltinGenericKind.Set => $"MutableSet<{TypeName(builtin.Arguments[0])}>",
			BuiltinGenericKind.Queue or BuiltinGenericKind.Stack or BuiltinGenericKind.Collection
				=> $"MutableList<{TypeName(builtin.Arguments[0])}>",
			BuiltinGenericKind.Task => $"kotlinx.coroutines.Deferred<{TypeName(builtin.Arguments[0])}>",
			BuiltinGenericKind.Iter or BuiltinGenericKind.AsyncIter or BuiltinGenericKind.Observable
				=> $"Iterable<{TypeName(builtin.Arguments[0])}>",
			_ => "Any"
		};
	}

	private string FunctionName(FunctionType function) =>
		"(" + string.Join(", ", function.Parameters.Select(TypeName)) + ") -> " + TypeName(function.ReturnType);

	private string TupleName(TupleType tuple) => tuple.Elements.Count switch
	{
		2 => $"Pair<{TypeName(tuple.Elements[0].Type)}, {TypeName(tuple.Elements[1].Type)}>",
		3 => $"Triple<{TypeName(tuple.Elements[0].Type)}, {TypeName(tuple.Elements[1].Type)}, " +
			$"{TypeName(tuple.Elements[2].Type)}>",
		_ => "List<Any?>"
	};

	private static string Primitive(PrimitiveType primitive) => primitive.Primitive switch
	{
		PrimitiveKind.Void => "Unit",
		PrimitiveKind.Bool => "Boolean",
		PrimitiveKind.Char => "Char",
		PrimitiveKind.Str => "String",
		PrimitiveKind.Object => "Any",
		PrimitiveKind.I8 => "Byte",
		PrimitiveKind.I16 => "Short",
		PrimitiveKind.I32 => "Int",
		PrimitiveKind.I64 => "Long",
		PrimitiveKind.U8 => "UByte",
		PrimitiveKind.U16 => "UShort",
		PrimitiveKind.U32 => "UInt",
		PrimitiveKind.U64 => "ULong",
		PrimitiveKind.F16 or PrimitiveKind.F32 => "Float",
		PrimitiveKind.F64 => "Double",
		PrimitiveKind.Inum => "Long",
		PrimitiveKind.Fnum or PrimitiveKind.Num => "Double",
		PrimitiveKind.Never => "Nothing",
		_ => "Any"
	};

	// ------------------------------------------------------------------ операторы

	protected override void DeclareLocal(string name, KernType? type, string? init, bool single)
	{
		if (type is null)
		{
			W.Line($"var {name}: Any? = {init ?? "null"}");
			return;
		}

		string keyword = single && init is not null ? "val" : "var";
		string value = init ?? (type is NullableType
			? "null"
			: type is PrimitiveType { Primitive: not PrimitiveKind.Object and not PrimitiveKind.Void }
				? ZeroOrNull(type)
				: "TODO()");
		W.Line($"{keyword} {name}: {TypeName(type)} = {value}");
	}

	protected override string ThrowText(string message) =>
		$"throw IllegalStateException(\"{Naming.Escape(message, true)}\")";

	protected override string AddToText(string list, string value) => $"{list}.add({value})";

	protected override string NewListText(KernType element) => $"mutableListOf<{TypeName(element)}>()";

	protected override void OpenWhile(string condition) => W.Open($"while ({condition})");

	protected override void OpenInfiniteLoop() => W.Open("while (true)");

	protected override void OpenDoWhileLoop() => W.Open("do");

	protected override void CloseDoWhile(string condition) => W.Close($" while ({condition})");

	// ------------------------------------------------------------------ выражения

	protected override string NumberLit(BoundLiteralExpression literal)
	{
		NumericLiteralValue? numeric = literal.Numeric;
		if (numeric is null)
			return "0";
		PrimitiveKind kind = literal.Type is PrimitiveType primitive
			? primitive.Primitive
			: numeric.IsFloat ? PrimitiveKind.F64 : PrimitiveKind.I32;
		string digits = numeric.IsFloat
			? numeric.FloatValue.ToString("R", CultureInfo.InvariantCulture)
			: numeric.IntegerValue.ToString(CultureInfo.InvariantCulture);
		if (kind is PrimitiveKind.F16 or PrimitiveKind.F32 or PrimitiveKind.F64 or PrimitiveKind.Fnum or PrimitiveKind.Num)
		{
			if (!digits.Contains('.') && !digits.Contains('E') && !digits.Contains('e'))
				digits += ".0";
			return kind is PrimitiveKind.F16 or PrimitiveKind.F32 ? digits + "f" : digits;
		}

		return kind switch
		{
			PrimitiveKind.I8 => $"{digits}.toByte()",
			PrimitiveKind.I16 => $"{digits}.toShort()",
			PrimitiveKind.I64 => digits + "L",
			PrimitiveKind.U8 => $"{digits}u.toUByte()",
			PrimitiveKind.U16 => $"{digits}u.toUShort()",
			PrimitiveKind.U32 => digits + "u",
			PrimitiveKind.U64 => digits + "uL",
			_ => digits
		};
	}

	protected override string NilLit => "null";
	protected override string NoneLit => "Unit";

	protected override string Ternary(string condition, string whenTrue, string whenFalse) =>
		$"(if ({condition}) {whenTrue} else {whenFalse})";

	protected override string Lambda(
		IReadOnlyList<(string Name, string Type)> parameters,
		string? expression,
		string? block,
		KernType returnType,
		bool isAsync)
	{
		string list = string.Join(", ", parameters.Select(parameter => $"{parameter.Name}: {parameter.Type}"));
		if (expression is not null)
			return $"{{ {list}{(parameters.Count > 0 ? " -> " : "")}{StripParens(expression)} }}";
		string result = IsVoid(returnType) ? "" : ": " + TypeName(returnType);
		return $"fun({list}){result} {{\n{block}\n}}";
	}

	protected override string ListLit(KernType element, IReadOnlyList<string> items) =>
		$"mutableListOf<{TypeName(element)}>({string.Join(", ", items)})";

	protected override string ArrayLit(KernType element, IReadOnlyList<string> items) =>
		$"arrayOf<{TypeName(element)}>({string.Join(", ", items)})";

	protected override string DictLit(KernType key, KernType value, IReadOnlyList<(string Key, string Value)> items) =>
		$"mutableMapOf<{TypeName(key)}, {TypeName(value)}>(" +
		string.Join(", ", items.Select(item => $"{item.Key} to {item.Value}")) + ")";

	protected override string TupleLit(IReadOnlyList<string> items) => items.Count switch
	{
		2 => $"Pair({items[0]}, {items[1]})",
		3 => $"Triple({items[0]}, {items[1]}, {items[2]})",
		_ => $"mutableListOf<Any?>({string.Join(", ", items)})"
	};

	protected override string Pow(string left, string right) => $"kernPow({left}, {right})";

	protected override string InText(string item, string collection, bool negated) =>
		$"kernIn({item}, {collection})" + (negated ? ".not()" : "");

	protected override string RefEq(string left, string right, bool negated) =>
		$"({left} {(negated ? "!==" : "===")} {right})";

	protected override string Cast(string subject, KernType type) => $"({subject} as {TypeName(type)})";

	protected override string IsType(string subject, KernType type, bool negated) =>
		$"({subject} {(negated ? "!is" : "is")} {TypeName(type)})";

	protected override string NonNullText(string value) => value + "!!";
	protected override string CoalesceText(string left, string right) => $"({left} ?: {right})";

	protected override string TupleItemText(string subject, int index, int count) =>
		count > 3 ? $"{subject}[{index}]" : $"{subject}.{(index switch { 0 => "first", 1 => "second", _ => "third" })}";

	protected override string LenText(string subject) => $"kernLen({subject})";

	protected override string RestSlice(string subject, string start, string end, bool inclusive) =>
		$"kernSlice({subject}, {start}, {end}, {(inclusive ? "true" : "false")})";

	protected override string ResultText(bool ok, string value, KernType type)
	{
		(KernType okType, KernType errorType) = ResultArguments(type);
		return $"KResult.{(ok ? "ok" : "err")}<{TypeName(okType)}, {TypeName(errorType)}>({value})";
	}

	protected override string ResultIsOk(string subject, bool ok) => ok ? $"{subject}.isOk" : $"!{subject}.isOk";
	protected override string ResultValue(string subject) => $"{subject}.value!!";
	protected override string ResultError(string subject) => $"{subject}.error!!";

	protected override string TypeOfText(KernType type) => $"{TypeName(type)}::class";
	protected override string TypeOfValueText(string value) => $"kernTypeOf({value})";
	protected override string AwaitText(string value) => value;
	protected override string NewText(KernType type, IReadOnlyList<string> args) =>
		$"{TypeName(type)}({string.Join(", ", args)})";

	protected override string FunctionRefText(string? receiver, string name, bool global) =>
		receiver is null ? $"::{name}" : $"{receiver}::{name}";

	protected override string ConvertText(string value, KernType from, KernType to) => to switch
	{
		PrimitiveType primitive => primitive.Primitive switch
		{
			PrimitiveKind.I8 => $"({value}).toByte()",
			PrimitiveKind.I16 => $"({value}).toShort()",
			PrimitiveKind.I32 => $"({value}).toInt()",
			PrimitiveKind.I64 or PrimitiveKind.Inum => $"({value}).toLong()",
			PrimitiveKind.U8 => $"({value}).toUByte()",
			PrimitiveKind.U16 => $"({value}).toUShort()",
			PrimitiveKind.U32 => $"({value}).toUInt()",
			PrimitiveKind.U64 => $"({value}).toULong()",
			PrimitiveKind.F16 or PrimitiveKind.F32 => $"({value}).toFloat()",
			PrimitiveKind.F64 or PrimitiveKind.Fnum or PrimitiveKind.Num => $"({value}).toDouble()",
			_ => value
		},
		_ => value
	};

	protected override string BaseAccess(KernType parent) => "super";
	protected override string NullDefault(KernType type) => "null";

	protected override string CallText(
		string? receiver,
		string name,
		IReadOnlyList<KernType> typeArguments,
		IReadOnlyList<string> arguments,
		bool safe)
	{
		string generic = typeArguments.Count == 0
			? ""
			: "<" + string.Join(", ", typeArguments.Select(TypeName)) + ">";
		string list = string.Join(", ", arguments);
		if (name.Length == 0)
			return $"{receiver}{(safe ? "?.invoke" : "")}({list})";
		return receiver is null
			? $"{name}{generic}({list})"
			: $"{receiver}{(safe ? "?." : ".")}{name}{generic}({list})";
	}

	protected override string GlobalRef(string name) => name;

	protected override string HashText(string value, bool structural) => $"kernHash({value})";
	protected override string CompareText(string left, string right, bool structural) =>
		$"kernCompare({left}, {right})";
	protected override string CloneText(string value) => $"kernClone({value})";

	protected override string ZeroOrNull(KernType? type) => type switch
	{
		PrimitiveType { Primitive: PrimitiveKind.Bool } => "false",
		PrimitiveType { Primitive: PrimitiveKind.Char } => "'\\u0000'",
		PrimitiveType { Primitive: PrimitiveKind.Str } => "\"\"",
		PrimitiveType { Primitive: PrimitiveKind.Object } => "null",
		PrimitiveType { Primitive: PrimitiveKind.I64 } => "0L",
		PrimitiveType { Primitive: PrimitiveKind.U64 } => "0uL",
		PrimitiveType { Primitive: PrimitiveKind.U8 } => "0u.toUByte()",
		PrimitiveType { Primitive: PrimitiveKind.U16 } => "0u.toUShort()",
		PrimitiveType { Primitive: PrimitiveKind.U32 } => "0u",
		PrimitiveType { Primitive: PrimitiveKind.I8 } => "0.toByte()",
		PrimitiveType { Primitive: PrimitiveKind.I16 } => "0.toShort()",
		PrimitiveType { Primitive: PrimitiveKind.I32 or PrimitiveKind.Inum } => "0",
		PrimitiveType { Primitive: PrimitiveKind.F32 or PrimitiveKind.F16 } => "0.0f",
		PrimitiveType { Primitive: PrimitiveKind.F64 or PrimitiveKind.Fnum or PrimitiveKind.Num } => "0.0",
		NullableType => "null",
		_ => "null"
	};
}
