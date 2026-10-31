using System.Globalization;
using Kern.Bytecode;
using Kern.Compiler.Lexing;
using Kern.Compiler.Semantics.Bound.Declarations;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Lib.Transpile.Core;
using Kern.Lib.Transpile.Mapping;

namespace Kern.Lib.Transpile.Targets.CSharp;

/// <summary>Синтаксис C# (языковые хуки; объявления — в CSharpLowerer.Decls.cs).</summary>
public sealed partial class CSharpLowerer(IMappings maps) : Lowerer(maps)
{
	public const string Namespace = "KernGenerated";

	public override string Id => "csharp";
	public override string Extension => ".cs";
	protected override string End => ";";
	protected override bool AlwaysStatement => false;
	protected override string DiscardText(string value) => $"_ = {value};";
	protected override string RuntimeFileName => "KernRuntime.cs";
	protected override string RuntimeText => CSharpRuntime.Text;

	protected override string FileFor(BoundModuleBody m) => Naming.Pascal(m.Module.Name.Replace('.', '_')) + ".cs";

	protected string ModuleClass(ModuleSymbol m) => Naming.Pascal(m.Name.Replace('.', '_')) + "Module";

	// ------------------------------------------------------------------ имена

	protected override string LocalName(string kernName)
	{
		var n = Naming.Camel(kernName);
		return Naming.CSharpKeywords.Contains(n) ? "@" + n : n;
	}

	protected override string MemberName(string kernName) => Naming.Pascal(kernName);

	protected override string TypeName(TypeSymbol t)
	{
		switch (t)
		{
			case PrimitiveType p: return Prim(p);
			case NilType: return "object?";
			case NoneType: return "Unit";
			case NeverType: return "void";
			case ErrorType: return "object";
			case TypeParamType tp: return tp.Param.Name;
			case NullableType n: return TypeName(n.Inner) + "?";
			case ListType l: return $"List<{ArgName(l.Element)}>";
			case ArrayType a: return $"{TypeName(a.Element)}[]";
			case DictType d: return $"Dictionary<{ArgName(d.Key)}, {ArgName(d.Value)}>";
			case ResultType r: return $"KResult<{ArgName(r.Ok)}, {ArgName(r.Err)}>";
			case TaskType tk: return IsVoid(tk.Result) ? "Task" : $"Task<{TypeName(tk.Result)}>";
			case TupleType tu: return "(" + string.Join(", ", tu.Items.Select(TypeName)) + ")";
			case FuncType f:
			{
				var ps = f.Params.Select(ArgName).ToList();
				if (IsVoid(f.Return)) return ps.Count == 0 ? "Action" : $"Action<{string.Join(", ", ps)}>";
				ps.Add(ArgName(f.Return));
				return $"Func<{string.Join(", ", ps)}>";
			}
			case NativeType nt:
			{
				var args = nt.Args.Select(ArgName).ToList();
				var mapped = Maps.MapType(Id, nt.Symbol.Name, args);
				if (mapped is not null) return mapped;
				Unmapped.Add("тип " + nt.Symbol.FullName);
				return Naming.Pascal(nt.Symbol.Name) + (args.Count == 0 ? "" : $"<{string.Join(", ", args)}>");
			}
			case UnionType: return "object";
			case DeclaredType dt:
				return dt.Decl.Name + (dt.Args.Count == 0 ? "" : $"<{string.Join(", ", dt.Args.Select(ArgName))}>");
			default: return "object";
		}
	}

	private string ArgName(TypeSymbol t) => IsVoid(t) ? "Unit" : TypeName(t);

	private static string Prim(PrimitiveType p) => p.Kind switch
	{
		PrimKind.Void => "void",
		PrimKind.Bool => "bool",
		PrimKind.Char => "char",
		PrimKind.Str => "string",
		PrimKind.Object => "object",
		PrimKind.I8 => "sbyte", PrimKind.I16 => "short", PrimKind.I32 => "int", PrimKind.I64 => "long",
		PrimKind.U8 => "byte", PrimKind.U16 => "ushort", PrimKind.U32 => "uint", PrimKind.U64 => "ulong",
		PrimKind.F16 => "Half", PrimKind.F32 => "float", PrimKind.F64 => "double",
		PrimKind.INum => "long", PrimKind.FNum or PrimKind.Num => "double",
		PrimKind.Type => "Type",
		_ => "object",
	};

	// ------------------------------------------------------------------ операторы

	protected override void DeclareLocal(string name, TypeSymbol? type, string? init, bool single)
	{
		string t = type is null ? "var" : TypeName(type);
		if (init is null) { W.Line($"{(type is null ? "object" : t)} {name} = default!;"); return; }
		bool needsType = type is not null && (init == "null" || init.Contains("=>") || init == "default" || type is NullableType && init.StartsWith("null"));
		W.Line($"{(needsType ? t : "var")} {name} = {init};");
	}

	protected override string ThrowText(string message) => $"throw new InvalidOperationException(\"{message}\");";
	protected override string AddToText(string list, string value) => $"{list}.Add({value});";
	protected override string NewListText(TypeSymbol element) => $"new List<{ArgName(element)}>()";

	protected override void OpenForRange(string name, string start, string end, bool inclusive, string? step, bool negativeStep)
	{
		string op = negativeStep ? (inclusive ? ">=" : ">") : (inclusive ? "<=" : "<");
		string inc = step is null ? $"{name}++" : $"{name} += {step}";
		W.Open($"for (var {name} = {start}; {name} {op} {end}; {inc})");
	}

	protected override void OpenForEach(string variable, string iterable, bool isAsync) =>
		W.Open($"{(isAsync ? "await " : "")}foreach (var {variable} in {iterable})");

	protected override string DeconstructVariable(IReadOnlyList<string> names) => "(" + string.Join(", ", names) + ")";
	protected override void CloseDoWhile(string condition) => W.Close($" while ({condition});");

	// ------------------------------------------------------------------ выражения

	protected override string NumberLit(NumberValue v, TypeSymbol type)
	{
		var kind = (type as PrimitiveType)?.Kind ?? (v.IsFloat ? PrimKind.F64 : PrimKind.I32);
		string digits = v.IsFloat ? v.Float.ToString("R", CultureInfo.InvariantCulture) : v.Integer.ToString();
		if (kind is PrimKind.F32 or PrimKind.F64 or PrimKind.F16 or PrimKind.FNum or PrimKind.Num)
		{
			if (!v.IsFloat) digits = v.Integer.ToString();
			if (!digits.Contains('.') && !digits.Contains('E') && !digits.Contains('e')) digits += ".0";
			return kind switch { PrimKind.F32 => digits + "f", PrimKind.F16 => $"(Half){digits}", _ => digits };
		}
		if (v.IsFloat) return digits;
		return kind switch
		{
			PrimKind.I8 => $"(sbyte){digits}",
			PrimKind.I16 => $"(short){digits}",
			PrimKind.I64 => digits + "L",
			PrimKind.U8 => $"(byte){digits}",
			PrimKind.U16 => $"(ushort){digits}",
			PrimKind.U32 => digits + "U",
			PrimKind.U64 => digits + "UL",
			PrimKind.INum => v.Integer > int.MaxValue || v.Integer < int.MinValue ? digits + "L" : digits,
			_ => digits,
		};
	}

	protected override string NilLit => "null";
	protected override string NoneLit => "default";
	protected override string Ternary(string cond, string a, string b) => $"({cond} ? {a} : {b})";

	protected override string Lambda(IReadOnlyList<(string Name, string Type)> ps, string? expression, string? block, TypeSymbol returnType, bool isAsync)
	{
		string head = (isAsync ? "async " : "") + "(" + string.Join(", ", ps.Select(p => $"{p.Type} {p.Name}")) + ") =>";
		return expression is not null ? $"{head} {StripParens(expression)}" : $"{head}\n{{\n{block}\n}}";
	}

	protected override string ListLit(TypeSymbol element, IReadOnlyList<string> items) =>
		items.Count == 0 ? $"new List<{ArgName(element)}>()" : $"new List<{ArgName(element)}> {{ {string.Join(", ", items)} }}";

	protected override string ArrayLit(TypeSymbol element, IReadOnlyList<string> items) =>
		$"new {TypeName(element)}[] {{ {string.Join(", ", items)} }}";

	protected override string DictLit(DictType type, IReadOnlyList<(string Key, string Value)> items) =>
		items.Count == 0
			? $"new {TypeName(type)}()"
			: $"new {TypeName(type)} {{ {string.Join(", ", items.Select(i => $"{{ {i.Key}, {i.Value} }}"))} }}";

	protected override string TupleLit(IReadOnlyList<string> items) => "(" + string.Join(", ", items) + ")";

	protected override string RangeLit(string? start, string? end, bool inclusive, string? step) =>
		$"KernRuntime.Range({start ?? "0"}, {end ?? "long.MaxValue"}, {(inclusive ? "true" : "false")}, {step ?? "1"})";

	protected override string Pow(string a, string b) => $"Math.Pow({a}, {b})";
	protected override string InText(string item, string collection, bool negated) => $"{(negated ? "!" : "")}KernRuntime.In({item}, {collection})";
	protected override string RefEq(string a, string b, bool negated) => $"{(negated ? "!" : "")}ReferenceEquals({a}, {b})";
	protected override string Cast(string subject, TypeSymbol type) => $"(({TypeName(type)}){subject})";
	protected override string IsType(string subject, TypeSymbol type, bool negated) => $"({subject} is {(negated ? "not " : "")}{TypeName(type)})";
	protected override string NonNullText(string x) => x + "!";
	protected override string CoalesceText(string a, string b) => $"({a} ?? {b})";
	protected override string TupleItemText(string subject, int index, int count) => $"{subject}.Item{index + 1}";
	protected override string SafeIndexText(string target, string index) => $"{target}?[{index}]";

	protected override string ResultText(bool ok, string? value, TypeSymbol type) =>
		$"{TypeName(type)}.{(ok ? "Ok" : "Err")}({value ?? "default"})";

	protected override string ResultIsOk(string s) => $"{s}.IsOk";
	protected override string ResultValue(string s) => $"{s}.Value";
	protected override string ResultError(string s) => $"{s}.Error";
	protected override string LenText(string subject) => $"KernRuntime.Len({subject})";
	protected override string RestSlice(string subject, int from, int tailCount) => $"KernRuntime.Slice({subject}, {from}, {tailCount})";

	protected override string Interpolated(IReadOnlyList<(string? Text, string? Value)> parts)
	{
		var sb = new System.Text.StringBuilder("$\"");
		foreach (var (text, value) in parts)
			sb.Append(text is not null ? Naming.EscapeInterpolatedText(text, false) : "{" + value + "}");
		return sb.Append('"').ToString();
	}

	protected override string TypeOfText(TypeSymbol type) => $"typeof({TypeName(type)})";
	protected override string AwaitText(string x) => $"await {x}";
	protected override string NewText(TypeSymbol type, IReadOnlyList<string> args) => $"new {TypeName(type)}({string.Join(", ", args)})";
	protected override string FunctionRefText(string? receiver, string name, bool isGlobal) => receiver is null ? name : $"{receiver}.{name}";

	protected override string ConvertText(string value, TypeSymbol from, TypeSymbol to) =>
		to is PrimitiveType { IsFloat: true } p && from is PrimitiveType { IsFloat: false } ? $"(({Prim(p)}){value})" : value;

	protected override string BaseAccess(TypeSymbol parent) => "base";
	protected override string NullDefault(TypeSymbol type) => "default";

	protected override string CallText(string? receiver, string name, IReadOnlyList<TypeSymbol> typeArgs, IReadOnlyList<string> args, bool safe)
	{
		string targs = typeArgs.Count == 0 ? "" : $"<{string.Join(", ", typeArgs.Select(ArgName))}>";
		string a = string.Join(", ", args);
		if (name.Length == 0) return $"{receiver}{(safe ? "?.Invoke" : "")}({a})";
		return receiver is null ? $"{name}{targs}({a})" : $"{receiver}{(safe ? "?." : ".")}{name}{targs}({a})";
	}

	protected override string GlobalRef(Symbol symbol, string name)
	{
		if (symbol.Container is not ModuleSymbol m) return name;
		if (m.IsStd) { Unmapped.Add("std: " + m.Name + "." + symbol.Name); return name; }
		return ReferenceEquals(m, Module.Module) ? name : $"{ModuleClass(m)}.{name}";
	}
}
