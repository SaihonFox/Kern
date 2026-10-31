using System.Globalization;
using Kern.Bytecode;
using Kern.Compiler.Lexing;
using Kern.Compiler.Semantics.Bound.Declarations;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Lib.Transpile.Core;
using Kern.Lib.Transpile.Mapping;

namespace Kern.Lib.Transpile.Targets.Kotlin;

/// <summary>Синтаксис Kotlin (языковые хуки; объявления — в KotlinLowerer.Decls.cs).</summary>
public sealed partial class KotlinLowerer(IMappings maps) : Lowerer(maps)
{
	public override string Id => "kotlin";
	public override string Extension => ".kt";
	protected override string End => "";
	protected override bool AlwaysStatement => true;
	protected override string DiscardText(string value) => value;
	protected override string RuntimeFileName => "KernRuntime.kt";
	protected override string RuntimeText => KotlinRuntime.Text;

	protected override string FileFor(BoundModuleBody m) => Naming.Pascal(m.Module.Name.Replace('.', '_')) + ".kt";
	protected string PackageOf(ModuleSymbol m) => m.Name.ToLowerInvariant();

	protected override string LocalName(string kernName)
	{
		var n = Naming.Camel(kernName);
		return Naming.KotlinKeywords.Contains(n) ? $"`{n}`" : n;
	}

	protected override string MemberName(string kernName)
	{
		var n = Naming.Camel(kernName);
		return Naming.KotlinKeywords.Contains(n) ? $"`{n}`" : n;
	}

	protected override string TypeName(TypeSymbol t)
	{
		switch (t)
		{
			case PrimitiveType p: return Prim(p);
			case NilType: return "Nothing?";
			case NoneType: return "Unit";
			case NeverType: return "Nothing";
			case ErrorType: return "Any";
			case TypeParamType tp: return tp.Param.Name;
			case NullableType n: return t is NullableType { Inner: FuncType } ? $"({TypeName(n.Inner)})?" : TypeName(n.Inner) + "?";
			case ListType l: return $"MutableList<{TypeName(l.Element)}>";
			case ArrayType a: return $"Array<{TypeName(a.Element)}>";
			case DictType d: return $"MutableMap<{TypeName(d.Key)}, {TypeName(d.Value)}>";
			case ResultType r: return $"KResult<{TypeName(r.Ok)}, {TypeName(r.Err)}>";
			case TaskType tk: return $"kotlinx.coroutines.Deferred<{TypeName(tk.Result)}>";
			case TupleType tu:
				if (tu.Items.Count == 2) return $"Pair<{TypeName(tu.Items[0])}, {TypeName(tu.Items[1])}>";
				if (tu.Items.Count == 3) return $"Triple<{TypeName(tu.Items[0])}, {TypeName(tu.Items[1])}, {TypeName(tu.Items[2])}>";
				Warnings.Add("кортежи длиннее 3 представлены как List<Any?>");
				return "List<Any?>";
			case FuncType f: return $"({string.Join(", ", f.Params.Select(TypeName))}) -> {TypeName(f.Return)}";
			case NativeType nt:
			{
				var args = nt.Args.Select(TypeName).ToList();
				var mapped = Maps.MapType(Id, nt.Symbol.Name, args);
				if (mapped is not null) return mapped;
				Unmapped.Add("тип " + nt.Symbol.FullName);
				return Naming.Pascal(nt.Symbol.Name) + (args.Count == 0 ? "" : $"<{string.Join(", ", args)}>");
			}
			case UnionType: return "Any";
			case DeclaredType dt:
				return dt.Decl.Name + (dt.Args.Count == 0 ? "" : $"<{string.Join(", ", dt.Args.Select(TypeName))}>");
			default: return "Any";
		}
	}

	private static string Prim(PrimitiveType p) => p.Kind switch
	{
		PrimKind.Void => "Unit",
		PrimKind.Bool => "Boolean",
		PrimKind.Char => "Char",
		PrimKind.Str => "String",
		PrimKind.Object => "Any",
		PrimKind.I8 => "Byte", PrimKind.I16 => "Short", PrimKind.I32 => "Int", PrimKind.I64 => "Long",
		PrimKind.U8 => "UByte", PrimKind.U16 => "UShort", PrimKind.U32 => "UInt", PrimKind.U64 => "ULong",
		PrimKind.F16 or PrimKind.F32 => "Float", PrimKind.F64 => "Double",
		PrimKind.INum => "Long", PrimKind.FNum or PrimKind.Num => "Double",
		PrimKind.Type => "kotlin.reflect.KClass<*>",
		_ => "Any",
	};

	// ------------------------------------------------------------------ операторы

	protected override void DeclareLocal(string name, TypeSymbol? type, string? init, bool single)
	{
		if (init is null) { W.Line($"var {name}: {(type is null ? "Any?" : TypeName(type))}{(type is null ? " = null" : "")}"); return; }
		bool typed = type is not null && (init == "null" || init.StartsWith("{") || init.StartsWith("fun("));
		W.Line($"{(single ? "val" : "var")} {name}{(typed ? ": " + TypeName(type!) : "")} = {init}");
	}

	protected override string ThrowText(string message) => $"throw IllegalStateException(\"{message}\")";
	protected override string AddToText(string list, string value) => $"{list}.add({value})";
	protected override string NewListText(TypeSymbol element) => $"mutableListOf<{TypeName(element)}>()";

	protected override void OpenForRange(string name, string start, string end, bool inclusive, string? step, bool negativeStep)
	{
		string range;
		if (negativeStep)
		{
			string s = (step ?? "-1").Trim('(', ')').TrimStart('-');
			range = $"{start} downTo {end}" + (s == "1" ? "" : $" step {s}");
		}
		else
		{
			range = inclusive ? $"{start}..{end}" : $"{start} until {end}";
			if (step is not null) range += $" step {step}";
		}
		W.Open($"for ({name} in {range})");
	}

	protected override void OpenForEach(string variable, string iterable, bool isAsync) => W.Open($"for ({variable} in {iterable})");
	protected override string DeconstructVariable(IReadOnlyList<string> names) => "(" + string.Join(", ", names) + ")";
	protected override void CloseDoWhile(string condition) => W.Close($" while ({condition})");

	// ------------------------------------------------------------------ выражения

	protected override string NumberLit(NumberValue v, TypeSymbol type)
	{
		var kind = (type as PrimitiveType)?.Kind ?? (v.IsFloat ? PrimKind.F64 : PrimKind.I32);
		string digits = v.IsFloat ? v.Float.ToString("R", CultureInfo.InvariantCulture) : v.Integer.ToString();
		if (kind is PrimKind.F32 or PrimKind.F64 or PrimKind.F16 or PrimKind.FNum or PrimKind.Num)
		{
			if (!digits.Contains('.') && !digits.Contains('E') && !digits.Contains('e')) digits += ".0";
			return kind is PrimKind.F32 or PrimKind.F16 ? digits + "f" : digits;
		}
		if (v.IsFloat) return digits;
		return kind switch
		{
			PrimKind.I8 => $"{digits}.toByte()",
			PrimKind.I16 => $"{digits}.toShort()",
			PrimKind.I64 => digits + "L",
			PrimKind.U8 => $"{digits}u.toUByte()",
			PrimKind.U16 => $"{digits}u.toUShort()",
			PrimKind.U32 => digits + "u",
			PrimKind.U64 => digits + "uL",
			PrimKind.INum => v.Integer > int.MaxValue || v.Integer < int.MinValue ? digits + "L" : digits,
			_ => digits,
		};
	}

	protected override string NilLit => "null";
	protected override string NoneLit => "Unit";
	protected override string Ternary(string cond, string a, string b) => $"(if ({cond}) {a} else {b})";

	protected override string Lambda(IReadOnlyList<(string Name, string Type)> ps, string? expression, string? block, TypeSymbol returnType, bool isAsync)
	{
		string plist = string.Join(", ", ps.Select(p => $"{p.Name}: {p.Type}"));
		if (expression is not null) return $"{{ {plist}{(ps.Count > 0 ? " -> " : "")}{StripParens(expression)} }}";
		string ret = IsVoid(returnType) ? "" : ": " + TypeName(returnType);
		return $"fun({plist}){ret} {{\n{block}\n}}";
	}

	protected override string ListLit(TypeSymbol element, IReadOnlyList<string> items) => $"mutableListOf<{TypeName(element)}>({string.Join(", ", items)})";
	protected override string ArrayLit(TypeSymbol element, IReadOnlyList<string> items) => $"arrayOf<{TypeName(element)}>({string.Join(", ", items)})";

	protected override string DictLit(DictType type, IReadOnlyList<(string Key, string Value)> items) =>
		$"mutableMapOf<{TypeName(type.Key)}, {TypeName(type.Value)}>({string.Join(", ", items.Select(i => $"{i.Key} to {i.Value}"))})";

	protected override string TupleLit(IReadOnlyList<string> items) => items.Count switch
	{
		2 => $"Pair({items[0]}, {items[1]})",
		3 => $"Triple({items[0]}, {items[1]}, {items[2]})",
		_ => $"mutableListOf<Any?>({string.Join(", ", items)})",
	};

	protected override string RangeLit(string? start, string? end, bool inclusive, string? step) =>
		$"kernRange(({start ?? "0"}).toLong(), ({end ?? "Long.MAX_VALUE"}).toLong(), {(inclusive ? "true" : "false")}, ({step ?? "1"}).toLong())";

	protected override string Pow(string a, string b) => $"Math.pow(({a}).toDouble(), ({b}).toDouble())";
	protected override string InText(string item, string collection, bool negated) => $"({item} {(negated ? "!in" : "in")} {collection})";
	protected override string RefEq(string a, string b, bool negated) => $"({a} {(negated ? "!==" : "===")} {b})";
	protected override string Cast(string subject, TypeSymbol type) => $"({subject} as {TypeName(type)})";
	protected override string IsType(string subject, TypeSymbol type, bool negated) => $"({subject} {(negated ? "!is" : "is")} {TypeName(type)})";
	protected override string NonNullText(string x) => x + "!!";
	protected override string CoalesceText(string a, string b) => $"({a} ?: {b})";

	protected override string TupleItemText(string subject, int index, int count) =>
		count > 3 ? $"{subject}[{index}]" : $"{subject}.{(index switch { 0 => "first", 1 => "second", _ => "third" })}";

	protected override string SafeIndexText(string target, string index) => $"{target}?.get({index})";

	protected override string ResultText(bool ok, string? value, TypeSymbol type)
	{
		var r = (ResultType)type;
		return $"KResult.{(ok ? "ok" : "err")}<{TypeName(r.Ok)}, {TypeName(r.Err)}>({value ?? "Unit"})";
	}

	protected override string ResultIsOk(string s) => $"{s}.isOk";
	protected override string ResultValue(string s) => $"{s}.value";
	protected override string ResultError(string s) => $"{s}.error";
	protected override string LenText(string subject) => $"kernLen({subject})";
	protected override string RestSlice(string subject, int from, int tailCount) => $"kernSlice({subject}, {from}, {tailCount})";

	protected override string Interpolated(IReadOnlyList<(string? Text, string? Value)> parts)
	{
		var sb = new System.Text.StringBuilder("\"");
		foreach (var (text, value) in parts)
			sb.Append(text is not null ? Naming.EscapeInterpolatedText(text, true) : "${" + value + "}");
		return sb.Append('"').ToString();
	}

	protected override string TypeOfText(TypeSymbol type) => $"{TypeName(type)}::class";
	protected override string AwaitText(string x) => x; // suspend-функции вызываются напрямую
	protected override string NewText(TypeSymbol type, IReadOnlyList<string> args) => $"{TypeName(type)}({string.Join(", ", args)})";
	protected override string FunctionRefText(string? receiver, string name, bool isGlobal) => receiver is null ? $"::{name}" : $"{receiver}::{name}";

	protected override string ConvertText(string value, TypeSymbol from, TypeSymbol to) => to is PrimitiveType p
		? p.Kind switch
		{
			PrimKind.I16 => $"({value}).toShort()",
			PrimKind.I32 => $"({value}).toInt()",
			PrimKind.I64 or PrimKind.INum => $"({value}).toLong()",
			PrimKind.F32 or PrimKind.F16 => $"({value}).toFloat()",
			PrimKind.F64 or PrimKind.FNum or PrimKind.Num => $"({value}).toDouble()",
			_ => value,
		}
		: value;

	protected override string BaseAccess(TypeSymbol parent) => "super";
	protected override string NullDefault(TypeSymbol type) => "null";

	protected override string CallText(string? receiver, string name, IReadOnlyList<TypeSymbol> typeArgs, IReadOnlyList<string> args, bool safe)
	{
		string targs = typeArgs.Count == 0 ? "" : $"<{string.Join(", ", typeArgs.Select(TypeName))}>";
		string a = string.Join(", ", args);
		if (name.Length == 0) return $"{receiver}{(safe ? "?.invoke" : "")}({a})";
		return receiver is null ? $"{name}{targs}({a})" : $"{receiver}{(safe ? "?." : ".")}{name}{targs}({a})";
	}

	protected override string GlobalRef(Symbol symbol, string name)
	{
		if (symbol.Container is ModuleSymbol { IsStd: true } m) Unmapped.Add("std: " + m.Name + "." + symbol.Name);
		return name;
	}
}
