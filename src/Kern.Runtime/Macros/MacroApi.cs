using Kern.Binding.Attributes;
using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Macros;

/// <summary>Диагностика, собранная макросом (<c>m.error</c>/<c>m.warn</c>).</summary>
public sealed record MacroDiagnostic(bool IsError, string Message);

/// <summary>Описание поля/параметра типа для <c>macro_ctx.decl.fields</c>.</summary>
[KernType("macro_field_info")]
public sealed class KernMacroField(
	string name,
	string type,
	string kind,
	string? defaultValue,
	bool isPublic)
{
	public string Name { get; } = name;
	public string Type { get; } = type;
	public string Kind { get; } = kind;
	public string? Default { get; } = defaultValue;
	public bool HasDefault => Default is not null;
	public bool IsPublic { get; } = isPublic;
}

/// <summary>Описание параметра метода макроса.</summary>
[KernType("macro_param_info")]
public sealed class KernMacroParam(string name, string type, string? defaultValue)
{
	public string Name { get; } = name;
	public string Type { get; } = type;
	public string? Default { get; } = defaultValue;
	public bool HasDefault => Default is not null;
}

/// <summary>Описание метода типа.</summary>
[KernType("macro_method_info")]
public sealed class KernMacroMethod(string name, string returnType, IReadOnlyList<KernMacroParam> parameters)
{
	public string Name { get; } = name;
	public string ReturnType { get; } = returnType;
	public IReadOnlyList<KernMacroParam> Parameters { get; } = parameters;
}

/// <summary>Описание атрибута (<c>#[entity("users")]</c>) для макроса.</summary>
[KernType("macro_attr_info")]
public sealed class KernMacroAttr(string name, IReadOnlyList<string> args)
{
	public string Name { get; } = name;
	public IReadOnlyList<string> Args { get; } = args;
}

/// <summary>Описание целевого типа в макросе (<c>m.decl</c>).</summary>
[KernType("macro_decl_info")]
public sealed class KernMacroDecl(
	string name,
	string kind,
	IReadOnlyList<string> generics,
	IReadOnlyList<KernMacroField> fields,
	IReadOnlyList<KernMacroMethod> methods,
	IReadOnlyList<KernMacroAttr> attrs,
	IReadOnlyList<string> parents,
	string text)
{
	public string Name { get; } = name;
	public string Kind { get; } = kind;
	public IReadOnlyList<string> Generics { get; } = generics;
	public IReadOnlyList<KernMacroField> Fields { get; } = fields;
	public IReadOnlyList<KernMacroMethod> Methods { get; } = methods;
	public IReadOnlyList<KernMacroAttr> Attrs { get; } = attrs;
	public IReadOnlyList<string> Parents { get; } = parents;
	public string Text { get; } = text;

	public KernMacroField? Field(string name) =>
		Fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal));

	public bool HasField(string name) => Field(name) is not null;

	public KernMacroMethod? Method(string name) =>
		Methods.FirstOrDefault(method => string.Equals(method.Name, name, StringComparison.Ordinal));

	public bool HasMethod(string name) => Method(name) is not null;

	public KernMacroAttr? Attribute(string name) =>
		Attrs.FirstOrDefault(attr => string.Equals(attr.Name, name, StringComparison.Ordinal));

	public bool HasAttribute(string name) => Attribute(name) is not null;
}

/// <summary>
/// Контекст макроса <c>macro_ctx</c> (spec round 22): описание цели и приёмник
/// сгенерированного текста. Макрос — обычная Kern-функция, исполняемая интерпретатором
/// до разбора основного проекта.
/// </summary>
[KernType("macro_ctx")]
public sealed class KernMacroContext(KernMacroDecl declaration, string name, string file, int line)
{
	private int _fresh;

	public KernMacroDecl Declaration { get; } = declaration;
	public string Name { get; } = name;
	public string File { get; } = file;
	public int Line { get; } = line;
	public List<string> Arguments { get; } = [];
	public List<string> Members { get; } = [];
	public List<string> Tops { get; } = [];
	public List<string> Expressions { get; } = [];
	public List<MacroDiagnostic> Diagnostics { get; } = [];

	public void Member(string code) => Members.Add(code);
	public void Top(string code) => Tops.Add(code);
	public void Expr(string code) => Expressions.Add(code);
	public void Error(string message) => Diagnostics.Add(new MacroDiagnostic(true, message));
	public void Warn(string message) => Diagnostics.Add(new MacroDiagnostic(false, message));
	public string Fresh(string prefix) => $"{prefix}{++_fresh}";
}

/// <summary>Члены <c>macro_ctx</c>.</summary>
[KernType("macro_ctx")]
public static class MacroContextApi
{
	[KernProperty]
	[KernReturn("macro_decl_info")]
	public static KValue Target(KNativeBox self) => KValue.Obj(new KNativeBox(Require(self).Declaration));

	[KernProperty]
	[KernName("decl")]
	[KernReturn("macro_decl_info")]
	public static KValue Declaration(KNativeBox self) => KValue.Obj(new KNativeBox(Require(self).Declaration));

	[KernProperty]
	[KernReturn("list<str>")]
	public static KValue Args(Interpreter interpreter, KNativeBox self) =>
		KValue.Obj(new KList(
			interpreter.Types.Prim(PrimKind.Str),
			[.. Require(self).Arguments.Select(KValue.Str)]));

	[KernProperty]
	public static string Name(KNativeBox self) => Require(self).Name;

	[KernProperty]
	public static string File(KNativeBox self) => Require(self).File;

	[KernProperty]
	public static int Line(KNativeBox self) => Require(self).Line;

	[KernMember]
	public static void Member(KNativeBox self, string code) => Require(self).Member(code);

	[KernMember]
	public static void Top(KNativeBox self, string code) => Require(self).Top(code);

	[KernMember]
	public static void Expr(KNativeBox self, string code) => Require(self).Expr(code);

	[KernMember]
	public static void Error(KNativeBox self, string message) => Require(self).Error(message);

	[KernName("warn")]
	[KernMember]
	public static void Warn(KNativeBox self, string message) => Require(self).Warn(message);

	[KernMember]
	public static string Quote(KNativeBox self, string text)
	{
		_ = self;
		return "\"" + text
			.Replace("\\", "\\\\", StringComparison.Ordinal)
			.Replace("\"", "\\\"", StringComparison.Ordinal)
			.Replace("\r", "\\r", StringComparison.Ordinal)
			.Replace("\n", "\\n", StringComparison.Ordinal)
			.Replace("\t", "\\t", StringComparison.Ordinal) + "\"";
	}

	[KernMember]
	public static string Fresh(KNativeBox self, string prefix) => Require(self).Fresh(prefix);

	private static KernMacroContext Require(KNativeBox self) => self.Value switch
	{
		KernMacroContext context => context,
		_ => throw new KernPanic($"macro_ctx: ожидался контекст макроса, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Члены <c>macro_decl_info</c>.</summary>
[KernType("macro_decl_info")]
public static class MacroDeclApi
{
	[KernProperty]
	public static string Name(KNativeBox self) => Require(self).Name;

	[KernProperty]
	public static string Kind(KNativeBox self) => Require(self).Kind;

	[KernProperty]
	[KernReturn("list<str>")]
	public static KValue Generics(Interpreter interpreter, KNativeBox self) =>
		StringList(interpreter, Require(self).Generics);

	[KernProperty]
	[KernReturn("list<macro_field_info>")]
	public static KValue Fields(Interpreter interpreter, KNativeBox self) =>
		KValue.Obj(new KList(
			interpreter.Types.Prim(PrimKind.Object),
			[.. Require(self).Fields.Select(field => KValue.Obj(new KNativeBox(field)))]));

	[KernProperty]
	[KernReturn("list<macro_method_info>")]
	public static KValue Methods(Interpreter interpreter, KNativeBox self) =>
		KValue.Obj(new KList(
			interpreter.Types.Prim(PrimKind.Object),
			[.. Require(self).Methods.Select(method => KValue.Obj(new KNativeBox(method)))]));

	[KernProperty]
	[KernReturn("list<macro_attr_info>")]
	public static KValue Attrs(Interpreter interpreter, KNativeBox self) =>
		KValue.Obj(new KList(
			interpreter.Types.Prim(PrimKind.Object),
			[.. Require(self).Attrs.Select(attr => KValue.Obj(new KNativeBox(attr)))]));

	[KernProperty]
	[KernReturn("list<str>")]
	public static KValue Parents(Interpreter interpreter, KNativeBox self) =>
		StringList(interpreter, Require(self).Parents);

	[KernProperty]
	public static string Text(KNativeBox self) => Require(self).Text;

	[KernName("has_attr")]
	[KernMember]
	public static bool HasAttribute(KNativeBox self, string name) => Require(self).HasAttribute(name);

	[KernMember]
	[KernReturn("macro_attr_info?")]
	public static KValue Attr(KNativeBox self, string name) =>
		Require(self).Attribute(name) is { } attribute
			? KValue.Obj(new KNativeBox(attribute))
			: KValue.Nil;

	[KernName("has_method")]
	[KernMember]
	public static bool HasMethod(KNativeBox self, string name) => Require(self).HasMethod(name);

	[KernMember]
	[KernReturn("macro_field_info?")]
	public static KValue Field(KNativeBox self, string name) =>
		Require(self).Field(name) is { } field
			? KValue.Obj(new KNativeBox(field))
			: KValue.Nil;

	private static KernMacroDecl Require(KNativeBox self) => self.Value switch
	{
		KernMacroDecl declaration => declaration,
		_ => throw new KernPanic($"macro_decl_info: ожидалось описание типа, получено {self.Value.GetType().Name}"),
	};

	private static KValue StringList(Interpreter interpreter, IReadOnlyList<string> values) =>
		KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Str), [.. values.Select(KValue.Str)]));
}

/// <summary>Члены <c>macro_field_info</c>.</summary>
[KernType("macro_field_info")]
public static class MacroFieldApi
{
	[KernProperty]
	public static string Name(KNativeBox self) => Require(self).Name;

	[KernProperty]
	public static string Type(KNativeBox self) => Require(self).Type;

	[KernProperty]
	public static string Kind(KNativeBox self) => Require(self).Kind;

	[KernProperty]
	[KernReturn("str?")]
	public static KValue Default(KNativeBox self) =>
		Require(self).Default is { } value ? KValue.Str(value) : KValue.Nil;

	[KernName("has_default")]
	[KernProperty]
	public static bool HasDefault(KNativeBox self) => Require(self).HasDefault;

	[KernName("is_public")]
	[KernProperty]
	public static bool IsPublic(KNativeBox self) => Require(self).IsPublic;

	private static KernMacroField Require(KNativeBox self) => self.Value switch
	{
		KernMacroField field => field,
		_ => throw new KernPanic($"macro_field_info: ожидалось поле, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Члены <c>macro_method_info</c>.</summary>
[KernType("macro_method_info")]
public static class MacroMethodApi
{
	[KernProperty]
	public static string Name(KNativeBox self) => Require(self).Name;

	[KernName("return_type")]
	[KernProperty]
	public static string ReturnType(KNativeBox self) => Require(self).ReturnType;

	[KernProperty]
	[KernReturn("list<macro_param_info>")]
	public static KValue Params(Interpreter interpreter, KNativeBox self) =>
		KValue.Obj(new KList(
			interpreter.Types.Prim(PrimKind.Object),
			[.. Require(self).Parameters.Select(parameter => KValue.Obj(new KNativeBox(parameter)))]));

	private static KernMacroMethod Require(KNativeBox self) => self.Value switch
	{
		KernMacroMethod method => method,
		_ => throw new KernPanic($"macro_method_info: ожидался метод, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Члены <c>macro_param_info</c>.</summary>
[KernType("macro_param_info")]
public static class MacroParamApi
{
	[KernProperty]
	public static string Name(KNativeBox self) => Require(self).Name;

	[KernProperty]
	public static string Type(KNativeBox self) => Require(self).Type;

	[KernProperty]
	[KernReturn("str?")]
	public static KValue Default(KNativeBox self) =>
		Require(self).Default is { } value ? KValue.Str(value) : KValue.Nil;

	[KernName("has_default")]
	[KernProperty]
	public static bool HasDefault(KNativeBox self) => Require(self).HasDefault;

	private static KernMacroParam Require(KNativeBox self) => self.Value switch
	{
		KernMacroParam parameter => parameter,
		_ => throw new KernPanic($"macro_param_info: ожидался параметр, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Члены <c>macro_attr_info</c>.</summary>
[KernType("macro_attr_info")]
public static class MacroAttrApi
{
	[KernProperty]
	public static string Name(KNativeBox self) => Require(self).Name;

	[KernProperty]
	[KernReturn("list<str>")]
	public static KValue Args(Interpreter interpreter, KNativeBox self) =>
		KValue.Obj(new KList(
			interpreter.Types.Prim(PrimKind.Str),
			[.. Require(self).Args.Select(KValue.Str)]));

	[KernMember]
	[KernReturn("str?")]
	public static KValue Arg(KNativeBox self, int index)
	{
		IReadOnlyList<string> args = Require(self).Args;
		return index >= 0 && index < args.Count ? KValue.Str(args[index]) : KValue.Nil;
	}

	[KernMember]
	[KernReturn("str?")]
	public static KValue Str(KNativeBox self, int index)
	{
		IReadOnlyList<string> args = Require(self).Args;
		if (index < 0 || index >= args.Count)
			return KValue.Nil;
		string text = args[index];
		return KValue.Str(text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text);
	}

	[KernMember]
	[KernReturn("str?")]
	public static KValue Named(KNativeBox self, string key)
	{
		IReadOnlyList<string> args = Require(self).Args;
		for (var index = 0; index + 1 < args.Count; index++)
			if (args[index] == key)
				return KValue.Str(args[index + 1] == "=" ? args[index + 2] : args[index + 1]);
		return KValue.Nil;
	}

	[KernName("matches")]
	[KernMember]
	public static bool Matches(KNativeBox self, string name) => Require(self).Name == name;

	private static KernMacroAttr Require(KNativeBox self) => self.Value switch
	{
		KernMacroAttr attr => attr,
		_ => throw new KernPanic($"macro_attr_info: ожидался атрибут, получено {self.Value.GetType().Name}"),
	};
}
