using System.Text;

namespace Kern.Binding.Catalog;

/// <summary>PascalCase/camelCase → snake_case: ToI32OrErr → to_i32_or_err, HTTPServer → http_server.</summary>
public static class SnakeCase
{
	public static string Of(string name)
	{
		if (name.Length == 0) return name;
		var sb = new StringBuilder(name.Length + 4);
		for (int i = 0; i < name.Length; i++)
		{
			char c = name[i];
			if (c == '_') { sb.Append('_'); continue; }
			if (char.IsUpper(c))
			{
				bool prevLowerOrDigit = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
				bool acronymEnd = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);
				if (sb.Length > 0 && sb[^1] != '_' && (prevLowerOrDigit || acronymEnd)) sb.Append('_');
				sb.Append(char.ToLowerInvariant(c));
			}
			else sb.Append(c);
		}
		return sb.ToString();
	}
}
