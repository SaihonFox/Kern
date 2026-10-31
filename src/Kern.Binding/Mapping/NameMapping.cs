using System.Text;

namespace Kern.Binding.Mapping;

/// <summary>
/// Имена C# → Kern и обратно (spec §12.7): по умолчанию Kern-имена — `snake_case`,
/// явные переопределения задаются <c>[KernName]</c>/<c>[KernType]</c>.
/// </summary>
public static class NameMapping
{
	/// <summary>`ReadAllText` → `read_all_text`, `IOError` → `io_error`, `URL` → `url`.</summary>
	public static string ToSnakeCase(string name)
	{
		ArgumentNullException.ThrowIfNull(name);
		if (name.Length == 0)
			return name;

		var builder = new StringBuilder(name.Length + 8);
		for (var index = 0; index < name.Length; index++)
		{
			char current = name[index];
			if (current == '_')
			{
				builder.Append('_');
				continue;
			}

			bool isUpper = char.IsUpper(current);
			if (isUpper && builder.Length > 0)
			{
				char previous = name[index - 1];
				bool previousIsLowerOrDigit = char.IsLower(previous) || char.IsDigit(previous);
				bool nextIsLower = index + 1 < name.Length && char.IsLower(name[index + 1]);
				bool acronymEnds = char.IsUpper(previous) && nextIsLower;
				if (previousIsLowerOrDigit || acronymEnds)
					builder.Append('_');
			}

			builder.Append(isUpper ? char.ToLowerInvariant(current) : current);
		}

		return builder.ToString();
	}

	/// <summary>`read_all_text` → `ReadAllText`; используется для обратного поиска.</summary>
	public static string ToPascalCase(string name)
	{
		ArgumentNullException.ThrowIfNull(name);
		var builder = new StringBuilder(name.Length);
		bool upperNext = true;
		foreach (char symbol in name)
		{
			if (symbol == '_')
			{
				upperNext = true;
				continue;
			}

			builder.Append(upperNext ? char.ToUpperInvariant(symbol) : symbol);
			upperNext = false;
		}

		return builder.ToString();
	}
}
