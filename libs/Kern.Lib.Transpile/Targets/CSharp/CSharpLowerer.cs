using System.Globalization;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Lib.Transpile.Core;
using Kern.Lib.Transpile.Mapping;

namespace Kern.Lib.Transpile.Targets.CSharp;

/// <summary>Синтаксис C# (языковые хуки; объявления — в <c>CSharpLowerer.Decls.cs</c>).</summary>
public sealed partial class CSharpLowerer(IMappings maps) : Lowerer(maps)
{
	public const string Namespace = "KernGenerated";

	public override string Id => "csharp";
	public override string Extension => ".cs";
	protected override string End => ";";
	protected override bool AlwaysStatement => false;
	protected override string DiscardText(string value) =>
		value is "null" or "default" ? "_ = (object?)null;" : $"_ = {value};";
	protected override string RuntimeFileName => "KernRuntime.cs";
	protected override string RuntimeText => CSharpRuntime.Text;

	protected override string FileFor(BoundModule module) =>
		Naming.Pascal(module.Symbol.Name.Replace('.', '_')) + ".cs";

	private string ModuleClass(BoundModule module) =>
		Naming.Pascal(module.Symbol.Name.Replace('.', '_')) + "Module";

	// ------------------------------------------------------------------ имена

	protected override string LocalName(string kernName)
	{
		string name = Naming.Camel(kernName);
		return Naming.CSharpKeywords.Contains(name) ? "@" + name : name;
	}

	protected override string MemberName(string kernName) => Naming.Pascal(kernName);

	protected override string TypeName(KernType type) => type switch
	{
		PrimitiveType primitive => Primitive(primitive),
		NilType => "object?",
		ErrorType => "object",
		NullableType nullable => TypeName(nullable.Underlying) + "?",
		ClassType @class => ClassName(@class.Symbol.Name, @class.Arguments),
		UnitType unit when ResultTypes.IsResultVariant(unit.Symbol) => "object",
		UnitType unit => ClassName(unit.Symbol.Name, unit.Arguments),
		TypeParameterType parameter => Naming.Sanitize(parameter.Name),
		FunctionType function => FunctionName(function),
		TupleType tuple => "(" + string.Join(", ", tuple.Elements.Select(element => TypeName(element.Type))) + ")",
		UnionType union => UnionName(union),
		BuiltinGenericType builtin => BuiltinName(builtin),
		_ => "object"
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
			BuiltinGenericKind.List => $"List<{TypeName(builtin.Arguments[0])}>",
			BuiltinGenericKind.Array => $"{TypeName(builtin.Arguments[0])}[]",
			BuiltinGenericKind.Dict => $"Dictionary<{TypeName(builtin.Arguments[0])}, {TypeName(builtin.Arguments[1])}>",
			BuiltinGenericKind.Set => $"HashSet<{TypeName(builtin.Arguments[0])}>",
			BuiltinGenericKind.Queue => $"Queue<{TypeName(builtin.Arguments[0])}>",
			BuiltinGenericKind.Stack => $"Stack<{TypeName(builtin.Arguments[0])}>",
			BuiltinGenericKind.Task => $"Task<{TypeName(builtin.Arguments[0])}>",
			BuiltinGenericKind.Iter or BuiltinGenericKind.AsyncIter or BuiltinGenericKind.Observable
				=> $"IEnumerable<{TypeName(builtin.Arguments[0])}>",
			BuiltinGenericKind.Channel or BuiltinGenericKind.Pipe or BuiltinGenericKind.Shared
				or BuiltinGenericKind.Collection => $"List<{TypeName(builtin.Arguments[0])}>",
			_ => "object"
		};
	}

	private string FunctionName(FunctionType function)
	{
		var parameters = function.Parameters.Select(TypeName).ToList();
		if (IsVoid(function.ReturnType))
			return parameters.Count == 0 ? "Action" : $"Action<{string.Join(", ", parameters)}>";
		parameters.Add(TypeName(function.ReturnType));
		return $"Func<{string.Join(", ", parameters)}>";
	}

	private static string Primitive(PrimitiveType primitive) => primitive.Primitive switch
	{
		PrimitiveKind.Void => "void",
		PrimitiveKind.Bool => "bool",
		PrimitiveKind.Char => "char",
		PrimitiveKind.Str => "string",
		PrimitiveKind.Object => "object",
		PrimitiveKind.I8 => "sbyte",
		PrimitiveKind.I16 => "short",
		PrimitiveKind.I32 => "int",
		PrimitiveKind.I64 => "long",
		PrimitiveKind.U8 => "byte",
		PrimitiveKind.U16 => "ushort",
		PrimitiveKind.U32 => "uint",
		PrimitiveKind.U64 => "ulong",
		PrimitiveKind.F16 => "Half",
		PrimitiveKind.F32 => "float",
		PrimitiveKind.F64 => "double",
		PrimitiveKind.Inum => "long",
		PrimitiveKind.Fnum or PrimitiveKind.Num => "double",
		PrimitiveKind.Never => "void",
		_ => "object"
	};

	// ------------------------------------------------------------------ операторы

	protected override void DeclareLocal(string name, KernType? type, string? init, bool single)
	{
		if (type is null)
		{
			W.Line($"object {name} = {init ?? "default!"};");
			return;
		}

		W.Line($"{TypeName(type)} {name} = {init ?? ZeroOrNull(type)};");
	}

	protected override string ThrowText(string message) =>
		$"throw new InvalidOperationException(\"{Naming.Escape(message, false)}\")";

	protected override string AddToText(string list, string value) => $"{list}.Add({value});";

	protected override string NewListText(KernType element) => $"new List<{TypeName(element)}>()";

	protected override void OpenWhile(string condition) => W.Open($"while ({condition})");

	protected override void OpenInfiniteLoop() => W.Open("while (true)");

	protected override void OpenDoWhileLoop() => W.Open("do");

	protected override void CloseDoWhile(string condition) => W.Close($" while ({condition});");

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
			return kind switch
			{
				PrimitiveKind.F32 => digits + "f",
				PrimitiveKind.F16 => $"(Half){digits}",
				_ => digits
			};
		}

		return kind switch
		{
			PrimitiveKind.I8 => $"(sbyte){digits}",
			PrimitiveKind.I16 => $"(short){digits}",
			PrimitiveKind.I64 => digits + "L",
			PrimitiveKind.U8 => $"(byte){digits}",
			PrimitiveKind.U16 => $"(ushort){digits}",
			PrimitiveKind.U32 => digits + "U",
			PrimitiveKind.U64 => digits + "UL",
			_ => digits
		};
	}

	protected override string NilLit => "null";
	protected override string NoneLit => "default";

	protected override string Ternary(string condition, string whenTrue, string whenFalse) =>
		$"({condition} ? {whenTrue} : {whenFalse})";

	protected override string Lambda(
		IReadOnlyList<(string Name, string Type)> parameters,
		string? expression,
		string? block,
		KernType returnType,
		bool isAsync)
	{
		string head = (isAsync ? "async " : "") + "(" +
			string.Join(", ", parameters.Select(parameter => $"{parameter.Type} {parameter.Name}")) + ") =>";
		return expression is not null
			? $"{head} {StripParens(expression)}"
			: $"{head}\n{{\n{block}\n}}";
	}

	protected override string ListLit(KernType element, IReadOnlyList<string> items) =>
		items.Count == 0
			? $"new List<{TypeName(element)}>()"
			: $"new List<{TypeName(element)}> {{ {string.Join(", ", items)} }}";

	protected override string ArrayLit(KernType element, IReadOnlyList<string> items) =>
		$"new {TypeName(element)}[] {{ {string.Join(", ", items)} }}";

	protected override string DictLit(KernType key, KernType value, IReadOnlyList<(string Key, string Value)> items) =>
		items.Count == 0
			? $"new Dictionary<{TypeName(key)}, {TypeName(value)}>()"
			: $"new Dictionary<{TypeName(key)}, {TypeName(value)}> {{ " +
				string.Join(", ", items.Select(item => $"{{ {item.Key}, {item.Value} }}")) + " }";

	protected override string TupleLit(IReadOnlyList<string> items) => "(" + string.Join(", ", items) + ")";

	protected override string Pow(string left, string right) => $"KernRuntime.Pow({left}, {right})";

	protected override string InText(string item, string collection, bool negated) =>
		$"{(negated ? "!" : "")}KernRuntime.In({item}, {collection})";

	protected override string RefEq(string left, string right, bool negated) =>
		$"{(negated ? "!" : "")}ReferenceEquals({left}, {right})";

	protected override string Cast(string subject, KernType type) => $"(({TypeName(type)}){subject})";

	protected override string IsType(string subject, KernType type, bool negated) =>
		$"({subject} is {(negated ? "not " : "")}{TypeName(type)})";

	protected override string NonNullText(string value) => value + "!";
	protected override string CoalesceText(string left, string right) => $"({left} ?? {right})";

	protected override string TupleItemText(string subject, int index, int count) => $"{subject}.Item{index + 1}";

	protected override string LenText(string subject) => $"KernRuntime.Len({subject})";

	protected override string RestSlice(string subject, string start, string end, bool inclusive) =>
		$"KernRuntime.Slice({subject}, {start}, {end}, {(inclusive ? "true" : "false")})";

	protected override string ResultText(bool ok, string value, KernType type)
	{
		(KernType okType, KernType errorType) = ResultArguments(type);
		return $"KResult<{TypeName(okType)}, {TypeName(errorType)}>.{(ok ? "Ok" : "Err")}({value})";
	}

	protected override string ResultIsOk(string subject, bool ok) => ok ? $"{subject}.IsOk" : $"!{subject}.IsOk";
	protected override string ResultValue(string subject) => $"{subject}.Value";
	protected override string ResultError(string subject) => $"{subject}.Error";

	protected override string TypeOfText(KernType type) => $"typeof({TypeName(type)})";
	protected override string TypeOfValueText(string value) => $"KernRuntime.TypeOf({value})";
	protected override string AwaitText(string value) => $"await {value}";
	protected override string NewText(KernType type, IReadOnlyList<string> args) =>
		$"new {TypeName(type)}({string.Join(", ", args)})";

	protected override string FunctionRefText(string? receiver, string name, bool global) =>
		receiver is null ? name : $"{receiver}.{name}";

	protected override string ConvertText(string value, KernType from, KernType to)
	{
		if (to is PrimitiveType target && from is PrimitiveType source &&
			IsNumeric(source.Primitive) && IsNumeric(target.Primitive))
			return $"(({Primitive(target)}){value})";
		return value;
	}

	protected override string BaseAccess(KernType parent) => "base";
	protected override string NullDefault(KernType type) => "default";

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
			return $"{receiver}{(safe ? "?.Invoke" : "")}({list})";
		return receiver is null
			? $"{name}{generic}({list})"
			: $"{receiver}{(safe ? "?." : ".")}{name}{generic}({list})";
	}

	protected override string GlobalRef(string name) => name;

	protected override string HashText(string value, bool structural) => $"KernRuntime.Hash({value})";

	protected override string CompareText(string left, string right, bool structural) =>
		$"KernRuntime.Compare({left}, {right})";

	protected override string CloneText(string value) => $"KernRuntime.Clone({value})";

	protected override string ZeroOrNull(KernType? type) => type switch
	{
		PrimitiveType { Primitive: PrimitiveKind.Bool } => "false",
		PrimitiveType { Primitive: PrimitiveKind.Char } => "'\\0'",
		PrimitiveType { Primitive: PrimitiveKind.Str or PrimitiveKind.Object } => "null!",
		PrimitiveType { Primitive: PrimitiveKind.I64 } => "0L",
		PrimitiveType { Primitive: PrimitiveKind.U64 } => "0UL",
		PrimitiveType { Primitive: PrimitiveKind.I8 or PrimitiveKind.I16 or PrimitiveKind.I32
			or PrimitiveKind.U8 or PrimitiveKind.U16 or PrimitiveKind.U32 } => "0",
		PrimitiveType { Primitive: PrimitiveKind.F32 } => "0f",
		PrimitiveType { Primitive: PrimitiveKind.F16 } => "(Half)0",
		PrimitiveType { Primitive: PrimitiveKind.F64 or PrimitiveKind.Fnum or PrimitiveKind.Num } => "0.0",
		NullableType => "null",
		_ => "default!"
	};

	private static bool IsNumeric(PrimitiveKind kind) => kind is
		PrimitiveKind.I8 or PrimitiveKind.I16 or PrimitiveKind.I32 or PrimitiveKind.I64 or
		PrimitiveKind.U8 or PrimitiveKind.U16 or PrimitiveKind.U32 or PrimitiveKind.U64 or
		PrimitiveKind.F16 or PrimitiveKind.F32 or PrimitiveKind.F64 or
		PrimitiveKind.Inum or PrimitiveKind.Fnum or PrimitiveKind.Num;
}
