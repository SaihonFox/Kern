using System.Text;

namespace Kern.Lib.Transpile.Core;

/// <summary>Преобразование имён Kern (snake_case) в соглашения целевых языков.</summary>
public static class Naming
{
	/// <summary>PascalCase для типов и членов; ведущие подчёркивания сохраняются.</summary>
	public static string Pascal(string s)
	{
		var builder = new StringBuilder();
		var upper = true;
		foreach (char symbol in s)
		{
			if (symbol is '_' or '.' or '#')
			{
				upper = true;
				continue;
			}

			builder.Append(upper ? char.ToUpperInvariant(symbol) : symbol);
			upper = false;
		}

		return builder.Length == 0 ? "_" : builder.ToString();
	}

	/// <summary>camelCase для локальных переменных; ведущие подчёркивания сохраняются.</summary>
	public static string Camel(string s)
	{
		string pascal = Pascal(s);
		var lead = 0;
		foreach (char symbol in s)
		{
			if (symbol != '_')
				break;
			lead++;
		}

		string body = lead >= pascal.Length ? pascal : char.ToLowerInvariant(pascal[lead]) + pascal[(lead + 1)..];
		return new string('_', lead) + body;
	}

	/// <summary>Служебные имена binder'а (<c>#iter3</c>, <c>$</c>) в допустимые имена целевого языка.</summary>
	public static string Local(string name) =>
		name switch
		{
			"$" => "self",
			_ => Sanitize(Camel(name))
		};

	public static string Member(string name) => Sanitize(Pascal(name));

	/// <summary>Оставляет буквы, цифры и подчёркивания; первый символ не может быть цифрой.</summary>
	public static string Sanitize(string name)
	{
		var builder = new StringBuilder(name.Length + 1);
		foreach (char symbol in name)
			builder.Append(char.IsLetterOrDigit(symbol) || symbol == '_' ? symbol : '_');
		if (builder.Length == 0)
			return "_";
		if (char.IsDigit(builder[0]))
			builder.Insert(0, '_');
		return builder.ToString();
	}

	/// <summary>Экранирование строкового литерала (Kotlin дополнительно экранирует <c>$</c>).</summary>
	public static string Escape(string s, bool kotlin)
	{
		var builder = new StringBuilder(s.Length + 4);
		foreach (char symbol in s)
		{
			switch (symbol)
			{
				case '\\': builder.Append("\\\\"); break;
				case '"': builder.Append("\\\""); break;
				case '\n': builder.Append("\\n"); break;
				case '\r': builder.Append("\\r"); break;
				case '\t': builder.Append("\\t"); break;
				case '$' when kotlin: builder.Append("\\$"); break;
				case '\0': builder.Append(kotlin ? "\\u0000" : "\\0"); break;
				default: builder.Append(symbol); break;
			}
		}

		return builder.ToString();
	}

	public static readonly HashSet<string> CSharpKeywords =
	[
		"abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
		"continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
		"false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
		"internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
		"params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
		"sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
		"uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while"
	];

	public static readonly HashSet<string> KotlinKeywords =
	[
		"as", "break", "class", "continue", "do", "else", "false", "for", "fun", "if", "in", "interface", "is",
		"null", "object", "package", "return", "super", "this", "throw", "true", "try", "typealias", "typeof",
		"val", "var", "when", "while"
	];
}
