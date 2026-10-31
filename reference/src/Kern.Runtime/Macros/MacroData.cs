using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Runtime.Reflection;

namespace Kern.Runtime.Macros;

/// <summary>Применённый к цели атрибут: имя и аргументы как исходный текст.</summary>
[KernType("macro_attr")]
public sealed class MacroAttr(string name, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> named)
{
	[KernProperty] public string Name { get; } = name;
	[KernProperty] public KList<string> Args => Kv.Strs(args);

	/// <summary>Позиционный аргумент как текст (`"abc"` — вместе с кавычками); nil, если его нет.</summary>
	[KernMember] public string? Arg(int index) => index >= 0 && index < args.Count ? args[index] : null;

	/// <summary>Именованный аргумент `#[x(path: "/a")]`; nil, если его нет.</summary>
	[KernMember] public string? Named(string key) => named.GetValueOrDefault(key);

	/// <summary>Строковое значение позиционного аргумента без кавычек.</summary>
	[KernMember] public string? Str(int index) => Arg(index) is { } a ? MacroText.Unquote(a) : null;
}

/// <summary>Поле, свойство или параметр: имя, тип и значение по умолчанию как исходный текст.</summary>
[KernType("macro_field")]
public sealed class MacroField(string name, string type, string kind, string? defaultText, bool isPublic)
{
	[KernProperty] public string Name { get; } = name;
	/// <summary>Тип как написан в исходнике (`list<str>`, `i32?`); пустая строка — тип выводится (`var`).</summary>
	[KernProperty] public string Type { get; } = type;
	/// <summary>"param" (первичный конструктор), "field" или "property".</summary>
	[KernProperty] public string Kind { get; } = kind;
	[KernProperty] public string? Default { get; } = defaultText;
	[KernProperty] public bool HasDefault => defaultText is not null;
	[KernProperty] public bool IsPublic { get; } = isPublic;
}

/// <summary>Метод типа: имя, параметры, возвращаемый тип.</summary>
[KernType("macro_method")]
public sealed class MacroMethod(string name, IReadOnlyList<MacroField> parameters, string? returnType, bool isStatic, bool isAbstract)
{
	[KernProperty] public string Name { get; } = name;
	[KernProperty] public KList<MacroField> Params => Wrapping.Wrap<MacroField>(parameters);
	[KernProperty] public string? Return { get; } = returnType;
	[KernProperty] public bool IsStatic { get; } = isStatic;
	[KernProperty] public bool IsAbstract { get; } = isAbstract;
}

/// <summary>Цель макроса: объявленный тип (type/unit/enum) с полями, методами и атрибутами. Данные — только чтение.</summary>
[KernType("macro_target")]
public sealed class MacroTarget(
	string name, string kind, IReadOnlyList<string> generics, IReadOnlyList<MacroField> fields,
	IReadOnlyList<MacroMethod> methods, IReadOnlyList<MacroAttr> attrs, IReadOnlyList<string> parents, string text)
{
	[KernProperty] public string Name { get; } = name;
	/// <summary>"type", "abstract", "unit", "unit!" или "enum".</summary>
	[KernProperty] public string Kind { get; } = kind;
	[KernProperty] public KList<string> Generics => Kv.Strs(generics);
	/// <summary>Параметры первичного конструктора и поля/свойства из тела — в порядке объявления.</summary>
	[KernProperty] public KList<MacroField> Fields => Wrapping.Wrap<MacroField>(fields);
	[KernProperty] public KList<MacroMethod> Methods => Wrapping.Wrap<MacroMethod>(methods);
	[KernProperty] public KList<MacroAttr> Attrs => Wrapping.Wrap<MacroAttr>(attrs);
	[KernProperty] public KList<string> Parents => Kv.Strs(parents);
	/// <summary>Исходный текст объявления целиком.</summary>
	[KernProperty] public string Text { get; } = text;

	[KernMember] public bool HasAttr(string attr) => attrs.Any(a => string.Equals(a.Name, attr, StringComparison.OrdinalIgnoreCase));
	[KernMember] public MacroAttr? Attr(string attr) => attrs.FirstOrDefault(a => string.Equals(a.Name, attr, StringComparison.OrdinalIgnoreCase));
	[KernMember] public bool HasMethod(string method) => methods.Any(m => m.Name == method);
	[KernMember] public MacroField? Field(string field) => fields.FirstOrDefault(f => f.Name == field);
}

internal static class MacroText
{
	/// <summary>Снимает кавычки с литерала `"abc"` (escape-последовательности `\n`, `\"`, `\\`, `\t` разбираются).</summary>
	public static string Unquote(string literal)
	{
		var s = literal.Trim();
		if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
		else return s;
		var sb = new System.Text.StringBuilder();
		for (int i = 0; i < s.Length; i++)
		{
			if (s[i] == '\\' && i + 1 < s.Length)
			{
				i++;
				sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => s[i] });
			}
			else sb.Append(s[i]);
		}
		return sb.ToString();
	}

	/// <summary>Строка → литерал Kern с экранированием (`"` `\` `$` переводы строк).</summary>
	public static string Quote(string s)
	{
		var sb = new System.Text.StringBuilder("\"");
		foreach (var c in s)
			sb.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => c.ToString() });
		return sb.Append('"').ToString();
	}
}
