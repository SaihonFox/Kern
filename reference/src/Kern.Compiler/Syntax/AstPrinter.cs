using System.Collections;
using System.Reflection;
using System.Text;
using Kern.Compiler.Lexing;
using Kern.Core;

namespace Kern.Compiler.Syntax;

/// <summary>Отладочный дамп AST в виде S-выражений: `(TypeName prop=value (Child ...) ...)`.</summary>
public static class AstPrinter
{
	public static string Print(Node node)
	{
		var sb = new StringBuilder();
		Write(sb, node, 0);
		return sb.ToString();
	}

	private static void Write(StringBuilder sb, Node node, int indent)
	{
		var type = node.GetType();
		sb.Append('(').Append(type.Name);
		var inline = new StringBuilder();
		var nested = new List<(string Name, object Value)>();

		foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.MetadataToken))
		{
			if (prop.GetIndexParameters().Length != 0 || prop.Name is "Span" or "NameSpan" or "File") continue;
			var value = prop.GetValue(node);
			if (value is null) continue;
			if (value is Node || (value is IEnumerable and not string && ContainsNode(value)))
				nested.Add((prop.Name, value));
			else if (value is IEnumerable seq and not string)
			{
				var items = seq.Cast<object?>().ToList();
				if (items.Count > 0) inline.Append(' ').Append(prop.Name).Append("=[").Append(string.Join(",", items.Select(Scalar))).Append(']');
			}
			else if (value is TextSpan or Modifiers) { if (value is Modifiers m && m != Modifiers.None) inline.Append(" mods=").Append(m.Access).Append('|').Append(m.Flags); }
			else if (value is bool b) { if (b) inline.Append(' ').Append(prop.Name); }
			else inline.Append(' ').Append(prop.Name).Append('=').Append(Scalar(value));
		}
		sb.Append(inline);

		foreach (var (name, value) in nested)
		{
			if (value is Node child)
			{
				NewLine(sb, indent + 1);
				sb.Append(name).Append(": ");
				Write(sb, child, indent + 1);
			}
			else
			{
				var children = ((IEnumerable)value).OfType<Node>().ToList();
				if (children.Count == 0) continue;
				NewLine(sb, indent + 1);
				sb.Append(name).Append(':');
				foreach (var c in children)
				{
					NewLine(sb, indent + 2);
					Write(sb, c, indent + 2);
				}
			}
		}
		sb.Append(')');
	}

	private static bool ContainsNode(object value) => ((IEnumerable)value).OfType<Node>().Any();

	private static void NewLine(StringBuilder sb, int indent) => sb.Append('\n').Append(' ', indent * 2);

	private static string Scalar(object? v) => v switch
	{
		null => "null",
		string s => '"' + s.Replace("\n", "\\n") + '"',
		NumberValue n => n.IsFloat ? n.Float.ToString(System.Globalization.CultureInfo.InvariantCulture) + n.Suffix
								   : n.Integer + (n.Suffix ?? ""),
		_ => v.ToString() ?? "",
	};
}
