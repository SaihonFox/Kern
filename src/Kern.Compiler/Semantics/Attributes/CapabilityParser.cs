using Kern.Bytecode;

namespace Kern.Compiler.Semantics.Attributes;

/// <summary>
/// Разбор <c>#[needs(...)]</c> (spec §7/§16): поддерживаются имена <c>net</c>,
/// <c>process</c>, <c>db</c>, <c>env</c> и <c>fs.read("scope")</c>/<c>fs.write("scope")</c>.
/// Парсер атрибутов хранит аргументы плоским списком токенов, поэтому разбираем его здесь.
/// </summary>
public static class CapabilityParser
{
	public const string NeedsAttribute = "needs";

	public static IReadOnlySet<string> FromAttributes(IReadOnlyList<AttributeUse> attributes)
	{
		var result = new HashSet<string>(StringComparer.Ordinal);
		foreach (AttributeUse attribute in attributes)
		{
			if (!string.Equals(attribute.Name, NeedsAttribute, StringComparison.Ordinal))
				continue;
			Parse(attribute.Args, result);
		}

		return result;
	}

	private static void Parse(IReadOnlyList<KConst> args, HashSet<string> result)
	{
		for (var index = 0; index < args.Count; index++)
		{
			string text = Text(args[index]);
			if (text == "fs" && index + 2 < args.Count && Text(args[index + 1]) == ".")
			{
				string operation = Text(args[index + 2]);
				if (operation is "read" or "write")
				{
					result.Add("fs." + operation);
					index += 2;
					// Необязательный scope: `fs.read("data/")`.
					if (index + 1 < args.Count && Text(args[index + 1]) == "(")
					{
						index += 2;
						while (index < args.Count && Text(args[index]) != ")")
							index++;
					}
					continue;
				}
			}

			if (text is "net" or "process" or "db" or "env" ||
				text.StartsWith("fs.", StringComparison.Ordinal))
				result.Add(text);
		}
	}

	private static string Text(KConst value) => value.Kind switch
	{
		KConstKind.Str => value.AsStr,
		KConstKind.I64 => value.Bits.ToString(System.Globalization.CultureInfo.InvariantCulture),
		KConstKind.Bool => value.AsBool ? "true" : "false",
		_ => value.Describe(),
	};
}
