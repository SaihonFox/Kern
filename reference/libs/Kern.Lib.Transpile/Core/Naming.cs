using System.Text;

namespace Kern.Lib.Transpile.Core;

/// <summary>Преобразование имён Kern (snake_case) в соглашения целевых языков.</summary>
public static class Naming
{
	public static string Pascal(string s)
	{
		var sb = new StringBuilder();
		bool up = true;
		foreach (char c in s)
		{
			if (c == '_' || c == '.') { up = true; continue; }
			sb.Append(up ? char.ToUpperInvariant(c) : c);
			up = false;
		}
		return sb.Length == 0 ? s : sb.ToString();
	}

	public static string Camel(string s)
	{
		var p = Pascal(s);
		// ведущие подчёркивания сохраняются: _x → _x
		int lead = s.TakeWhile(c => c == '_').Count();
		return new string('_', lead) + (p.Length == 0 ? p : char.ToLowerInvariant(p[0]) + p[1..]);
	}

	public static string Escape(string s, bool kotlin)
	{
		var sb = new StringBuilder();
		foreach (char c in s)
		{
			switch (c)
			{
				case '\\': sb.Append("\\\\"); break;
				case '"': sb.Append("\\\""); break;
				case '\n': sb.Append("\\n"); break;
				case '\r': sb.Append("\\r"); break;
				case '\t': sb.Append("\\t"); break;
				case '$' when kotlin: sb.Append("\\$"); break;
				case '\0': sb.Append(kotlin ? "\\u0000" : "\\0"); break;
				default: sb.Append(c); break;
			}
		}
		return sb.ToString();
	}

	public static string EscapeInterpolatedText(string s, bool kotlin)
	{
		var e = Escape(s, kotlin);
		return kotlin ? e : e.Replace("{", "{{").Replace("}", "}}");
	}

	public static readonly HashSet<string> CSharpKeywords =
	[
		"abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue","decimal","default","delegate","do","double",
		"else","enum","event","explicit","extern","false","finally","fixed","float","for","foreach","goto","if","implicit","in","int","interface","internal",
		"is","lock","long","namespace","new","null","object","operator","out","override","params","private","protected","public","readonly","ref","return",
		"sbyte","sealed","short","sizeof","stackalloc","static","string","struct","switch","this","throw","true","try","typeof","uint","ulong","unchecked",
		"unsafe","ushort","using","virtual","void","volatile","while",
	];

	public static readonly HashSet<string> KotlinKeywords =
	[
		"as","break","class","continue","do","else","false","for","fun","if","in","interface","is","null","object","package","return","super","this","throw",
		"true","try","typealias","typeof","val","var","when","while",
	];
}
