using Kern.Bytecode;
using Kern.Compiler.Lexing;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Attributes;

/// <summary>
/// Преобразование разобранных атрибутов (<see cref="AttributeNode"/>) в байткодовые
/// <see cref="AttributeUse"/>: имя сохраняется ровно как написано, аргументы — в
/// <see cref="KConst"/>. Литералы разбираются по тексту токена; идентификаторы
/// (протоколы <c>#[derive(hash)]</c>, цели <c>#[cfg(test)]</c>) хранятся строкой,
/// поэтому нелитеральные аргументы не теряются (текущее поведение raw-text, B7).
/// </summary>
public static class AttributeValues
{
	/// <summary>Список атрибутов объявления в виде, готовом для символа и <c>.kbc</c>.</summary>
	public static IReadOnlyList<AttributeUse> Build(IReadOnlyList<AttributeNode>? attributes)
	{
		if (attributes is null || attributes.Count == 0)
			return [];

		var result = new List<AttributeUse>(attributes.Count);
		foreach (var attribute in attributes)
			result.Add(new AttributeUse(attribute.Name, [.. attribute.Arguments.Select(ParseArgument)]));
		return result;
	}

	/// <summary>
	/// Текст одного аргумента как константа: bool/nil/none, целые и вещественные литералы
	/// (суффиксы и <c>_</c>-разделители — через <see cref="NumericLiteralValue"/>), строки,
	/// символы; всё остальное — строкой с исходным текстом.
	/// </summary>
	private static KConst ParseArgument(string text)
	{
		switch (text)
		{
			case "true": return KConst.Bool(true);
			case "false": return KConst.Bool(false);
			case "nil": return KConst.Nil;
			case "none": return KConst.None;
		}

		if (text.Length > 0 && text[0] == '"' && StringLiteralText.TryDecode(text, out var value, out _))
			return KConst.Str(value);
		if (text.Length > 0 && text[0] == '\'' && StringLiteralText.TryDecodeChar(text, out var character, out _))
			return KConst.Char(character);

		if (NumericLiteralValue.TryParse(text, out var number))
		{
			if (number.IsFloat)
				return KConst.F64(number.FloatValue);
			if (number.IntegerValue >= long.MinValue && number.IntegerValue <= long.MaxValue)
				return KConst.I64((long)number.IntegerValue);
		}

		return KConst.Str(text);
	}
}
