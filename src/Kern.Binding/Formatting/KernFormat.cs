using System.Globalization;
using System.Text;
using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Binding.Formatting;

/// <summary>Отображение Kern-значений для `print`/`println` и интерполяции.</summary>
public static class KernFormat
{
	public static string Value(KValue value) => value.Kind switch
	{
		ValueKind.None => "none",
		ValueKind.Nil => "nil",
		ValueKind.Void => "void",
		ValueKind.Str => value.AsStr,
		ValueKind.Ref => FormatRef(value.Ref),
		ValueKind.Bool => value.AsBool ? "true" : "false",
		_ => value.ToString(),
	};

	/// <summary>Строковое представление для `${...}`: str подставляется без кавычек.</summary>
	public static string Interpolate(KValue value) => Value(value);

	/// <summary>
	/// Проверка спецификации формата интерполяции (spec §3): <c>[&lt;|&gt;][0]ширина[.точность][f|d|s]</c>.
	/// <c>null</c>/пустая строка — формат по умолчанию. Ошибка — по-русски.
	/// </summary>
	public static bool TryValidateSpec(string? spec, out string? error)
	{
		error = null;
		return spec is null || TryParseSpec(spec, out _, out error);
	}

	/// <summary>
	/// Отформатировать значение по спецификации <c>&lt;10</c>, <c>08d</c>, <c>.2f</c>, <c>&gt;6</c>.
	/// Неверная спецификация — <see cref="KernPanic"/> (binder проверяет литералы заранее).
	/// </summary>
	public static string Format(KValue value, string spec)
	{
		if (!TryParseSpec(spec, out var parsed, out var error))
			throw new KernPanic(error ?? $"неверный формат '{spec}'");

		var text = Render(value, parsed);
		if (parsed.Width <= text.Length)
			return text;
		if (parsed.ZeroPad)
			return ZeroPad(text, parsed.Width);
		return parsed.LeftAlign ? text.PadRight(parsed.Width) : text.PadLeft(parsed.Width);
	}

	private readonly record struct FormatSpec(bool LeftAlign, bool ZeroPad, int Width, int Precision, char Kind);

	private static bool TryParseSpec(string spec, out FormatSpec parsed, out string? error)
	{
		parsed = default;
		error = null;
		var index = 0;
		var leftAlign = false;
		if (index < spec.Length && spec[index] is '<' or '>')
		{
			leftAlign = spec[index] == '<';
			index++;
		}

		var zeroPad = index < spec.Length && spec[index] == '0';
		if (zeroPad)
			index++;

		var widthStart = index;
		while (index < spec.Length && char.IsAsciiDigit(spec[index]))
			index++;
		var width = index > widthStart
			? int.Parse(spec[widthStart..index], CultureInfo.InvariantCulture)
			: 0;

		var precision = -1;
		if (index < spec.Length && spec[index] == '.')
		{
			index++;
			var precisionStart = index;
			while (index < spec.Length && char.IsAsciiDigit(spec[index]))
				index++;
			if (index == precisionStart)
			{
				error = $"в формате '{spec}' после '.' нужна точность";
				return false;
			}

			precision = int.Parse(spec[precisionStart..index], CultureInfo.InvariantCulture);
		}

		var kind = '\0';
		if (index < spec.Length)
		{
			kind = spec[index];
			if (kind is not ('f' or 'd' or 's'))
			{
				error = $"неизвестный формат '{spec}': ожидался f, d или s";
				return false;
			}

			index++;
		}

		if (index != spec.Length)
		{
			error = $"неверный формат '{spec}'";
			return false;
		}

		parsed = new FormatSpec(leftAlign, zeroPad, width, precision, kind);
		return true;
	}

	private static string Render(KValue value, FormatSpec spec)
	{
		if (spec.Precision >= 0 && value.Kind is ValueKind.F16 or ValueKind.F32 or ValueKind.F64)
		{
			return value.AsF64.ToString(
				"F" + spec.Precision.ToString(CultureInfo.InvariantCulture),
				CultureInfo.InvariantCulture);
		}

		return Value(value);
	}

	private static string ZeroPad(string text, int width)
	{
		var zeros = new string('0', width - text.Length);
		return text.StartsWith('-') ? "-" + zeros + text[1..] : zeros + text;
	}

	private static string FormatRef(object? reference) => reference switch
	{
		null => "nil",
		KList list => FormatList(list.Items),
		KArray array => FormatList(array.Items.ToArray()),
		KDict dict => FormatDict(dict),
		KTuple tuple => $"({string.Join(", ", tuple.Items.ToArray().Select(Value))})",
		KObject obj => FormatObject(obj),
		KNativeBox box => box.Value.ToString() ?? "native",
		_ => reference.ToString() ?? "object",
	};

	private static string FormatList(IReadOnlyList<KValue> items)
	{
		if (items.Count == 0)
			return "[]";
		var builder = new StringBuilder("[");
		for (var index = 0; index < items.Count; index++)
		{
			if (index > 0)
				builder.Append(", ");
			builder.Append(items[index].Kind == ValueKind.Str ? items[index].AsStr : Value(items[index]));
		}

		return builder.Append(']').ToString();
	}

	private static string FormatDict(KDict dict)
	{		var entries = dict.Items.ToList();
		if (entries.Count == 0)
			return "{}";
		var builder = new StringBuilder("{");
		for (var index = 0; index < entries.Count; index++)
		{
			if (index > 0)
				builder.Append(", ");
			builder.Append(Value(entries[index].Key)).Append(": ").Append(Value(entries[index].Value));
		}

		return builder.Append('}').ToString();
	}

	/// <summary>
	/// Значение <c>unit</c>/<c>type</c>: <c>Name(field=value, …)</c> в порядке объявления
	/// (spec §9, golden-конвенция). Вложенные значения печатаются как в коллекциях.
	/// </summary>
	private static string FormatObject(KObject obj)
	{
		var fields = obj.Type.Definition.Fields;
		var builder = new StringBuilder(obj.Type.Definition.Name).Append('(');
		var printed = 0;
		for (var index = 0; index < obj.FieldCount; index++)
		{
			// Служебные слоты раскладки (padding) не являются членами объекта.
			if (index < fields.Count && fields[index].Flags.HasFlag(MemberFlags.Padding))
				continue;
			if (printed++ > 0)
				builder.Append(", ");
			var name = index < fields.Count ? fields[index].Name : index.ToString(System.Globalization.CultureInfo.InvariantCulture);
			builder.Append(name).Append('=').Append(Repr(obj.GetField(index)));
		}

		return builder.Append(')').ToString();
	}

	/// <summary>Представление внутри коллекций/полей: строки и символы в кавычках.</summary>
	private static string Repr(KValue value) => value.Kind switch
	{
		ValueKind.Str => "\"" + value.AsStr.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
		ValueKind.Char => "'" + value.AsChar + "'",
		_ => Value(value),
	};
}
