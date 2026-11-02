using System.Text;
using Kern.Binding.Attributes;
using Kern.Bytecode;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Ini;

/// <summary>
/// <c>kern.ini</c> (spec §16): INI-файлы с секциями <c>[name]</c>, парами
/// <c>key = value</c> и комментариями <c>;</c>/<c>#</c>. Значения хранятся строками.
/// </summary>
[KernModule("kern.ini")]
public static class IniApi
{
	/// <summary><c>ini.parse(text)</c>: секции → словарь; глобальные ключи — секция <c>""</c>.</summary>
	[KernMember]
	[KernReturn("result<dict<str, dict<str, str>>, str>")]
	public static KValue Parse(Interpreter interpreter, string text)
	{
		var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
		{
			[""] = new(StringComparer.Ordinal)
		};
		string current = "";
		string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
		for (var index = 0; index < lines.Length; index++)
		{
			string line = lines[index].Trim();
			if (line.Length == 0 || line[0] is ';' or '#')
				continue;
			if (line[0] == '[' && line[^1] == ']')
			{
				current = line[1..^1].Trim();
				if (!sections.ContainsKey(current))
					sections[current] = new(StringComparer.Ordinal);
				continue;
			}

			int equals = line.IndexOf('=');
			if (equals <= 0)
				return Failure(interpreter, $"ini: строка {index + 1}: ожидалось key = value");
			sections[current][line[..equals].Trim()] = line[(equals + 1)..].Trim();
		}

		var root = new KDict(interpreter.Types.Prim(PrimKind.Str), interpreter.Types.Prim(PrimKind.Object));
		foreach (var (name, values) in sections)
		{
			var section = new KDict(interpreter.Types.Prim(PrimKind.Str), interpreter.Types.Prim(PrimKind.Str));
			foreach (var (key, value) in values)
				section.Set(KValue.Str(key), KValue.Str(value));
			root.Set(KValue.Str(name), KValue.Obj(section));
		}

		return KernResults.Ok(
			interpreter,
			interpreter.Types.Builtin(BuiltinGenericKind.Dict,
				interpreter.Types.Prim(PrimKind.Str),
				interpreter.Types.Builtin(BuiltinGenericKind.Dict,
					interpreter.Types.Prim(PrimKind.Str),
					interpreter.Types.Prim(PrimKind.Str))),
			KValue.Obj(root));
	}

	/// <summary><c>ini.to_str(data)</c>: обратная запись секций.</summary>
	[KernMember]
	[KernName("to_str")]
	public static string ToStr(KValue data)
	{
		if (data.Ref is not KDict root)
			throw new KernPanic($"ini.to_str: ожидался dict, получено {data.TypeName}");
		var builder = new StringBuilder();
		foreach (var (name, value) in root.Items)
		{
			if (value.Ref is not KDict section)
				throw new KernPanic("ini.to_str: значения секций должны быть dict");
			if (builder.Length > 0)
				builder.Append('\n');
			if (name.AsStr.Length > 0)
				builder.Append('[').Append(name.AsStr).Append("]\n");
			foreach (var (key, item) in section.Items)
				builder.Append(key.AsStr).Append(" = ").Append(item.AsStr).Append('\n');
		}

		return builder.ToString();
	}

	private static KValue Failure(Interpreter interpreter, string message) =>
		KernResults.Err(interpreter, interpreter.Types.Prim(PrimKind.Str), KValue.Str(message));
}
