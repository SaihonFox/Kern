using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Native;

/// <summary>
/// Разбор Kern-имён типов из <c>NativeCatalog</c>: <c>i32</c>, <c>str</c>,
/// <c>list&lt;T&gt;</c>, <c>dict&lt;K,V&gt;</c>, <c>fun(i32)-&gt;str</c>, <c>T?</c>.
/// </summary>
internal static class NativeTypeParser
{
	public static KernType Parse(
		string text,
		IReadOnlyDictionary<string, TypeParameterType> variables,
		Func<string, KernType?>? resolveNamed = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(text);
		string value = text.Trim();

		if (value.EndsWith('?'))
			return NullableType.Create(Parse(value[..^1], variables, resolveNamed));

		if (value.StartsWith("fun(", StringComparison.Ordinal))
		{
			int close = FindMatching(value, 3, '(', ')');
			if (close < 0 || close + 2 >= value.Length || !value[(close + 1)..].StartsWith("->", StringComparison.Ordinal))
				throw new FormatException($"некорректный тип функции '{text}'");
			IReadOnlyList<KernType> parameters = string.IsNullOrWhiteSpace(value[4..close])
				? []
				: ParseList(value[4..close], variables, resolveNamed);
			KernType result = Parse(value[(close + 3)..], variables, resolveNamed);
			return new FunctionType(parameters, result);
		}

		int open = TopLevelOpenBracket(value);
		if (open < 0)
		{
			if (variables.TryGetValue(value, out TypeParameterType? variable))
				return variable;
			if (TryPrimitive(value, out KernType? primitive))
				return primitive;
			if (value == "type")
				return PrimitiveType.Object;
			if (resolveNamed?.Invoke(value) is { } named)
				return named;
			throw new FormatException($"неизвестный Kern-тип '{text}'");
		}

		if (!value.EndsWith('>'))
			throw new FormatException($"некорректный generic-тип '{text}'");
		string name = value[..open];
		IReadOnlyList<KernType> arguments = ParseList(value[(open + 1)..^1], variables, resolveNamed);
		return name switch
		{
			"list" => new BuiltinGenericType(BuiltinGenericKind.List, arguments),
			"array" => new BuiltinGenericType(BuiltinGenericKind.Array, arguments),
			"dict" => new BuiltinGenericType(BuiltinGenericKind.Dict, arguments),
			"set" => new BuiltinGenericType(BuiltinGenericKind.Set, arguments),
			"queue" => new BuiltinGenericType(BuiltinGenericKind.Queue, arguments),
			"stack" => new BuiltinGenericType(BuiltinGenericKind.Stack, arguments),
			"result" => new BuiltinGenericType(BuiltinGenericKind.Result, arguments),
			"task" => new BuiltinGenericType(BuiltinGenericKind.Task, arguments),
			"event" => new BuiltinGenericType(BuiltinGenericKind.Event, arguments),
			"observable" => new BuiltinGenericType(BuiltinGenericKind.Observable, arguments),
			"channel" => new BuiltinGenericType(BuiltinGenericKind.Channel, arguments),
			"pipe" => new BuiltinGenericType(BuiltinGenericKind.Pipe, arguments),
			"shared" => new BuiltinGenericType(BuiltinGenericKind.Shared, arguments),
			"iter" => new BuiltinGenericType(BuiltinGenericKind.Iter, arguments),
			"async_iter" => new BuiltinGenericType(BuiltinGenericKind.AsyncIter, arguments),
			"collection" => new BuiltinGenericType(BuiltinGenericKind.Collection, arguments),
			_ => throw new FormatException($"неизвестный generic-тип '{name}'"),
		};
	}

	private static IReadOnlyList<KernType> ParseList(
		string text,
		IReadOnlyDictionary<string, TypeParameterType> variables,
		Func<string, KernType?>? resolveNamed)
	{
		if (string.IsNullOrWhiteSpace(text))
			return [];
		var parts = SplitTopLevel(text, ',');
		var result = new List<KernType>(parts.Count);
		foreach (string part in parts)
			result.Add(Parse(part, variables, resolveNamed));
		return result;
	}

	private static List<string> SplitTopLevel(string text, char separator)
	{
		var parts = new List<string>();
		int depth = 0;
		int start = 0;
		for (var index = 0; index < text.Length; index++)
		{
			char symbol = text[index];
			if (symbol is '<' or '(')
				depth++;
			else if (symbol is '>' or ')')
				depth--;
			else if (symbol == separator && depth == 0)
			{
				parts.Add(text[start..index]);
				start = index + 1;
			}
		}

		parts.Add(text[start..]);
		return parts;
	}

	private static int TopLevelOpenBracket(string text)
	{
		int depth = 0;
		for (var index = 0; index < text.Length; index++)
		{
			char symbol = text[index];
			if (symbol == '<' && depth == 0)
				return index;
			if (symbol is '<' or '(')
				depth++;
			else if (symbol is '>' or ')')
				depth--;
		}

		return -1;
	}

	private static int FindMatching(string text, int openIndex, char open, char close)
	{
		int depth = 0;
		for (int index = openIndex; index < text.Length; index++)
		{
			if (text[index] == open)
				depth++;
			else if (text[index] == close)
			{
				depth--;
				if (depth == 0)
					return index;
			}
		}

		return -1;
	}

	private static bool TryPrimitive(string name, out KernType type)
	{
		type = name switch
		{
			"i8" => PrimitiveType.I8,
			"i16" => PrimitiveType.I16,
			"i32" => PrimitiveType.I32,
			"i64" => PrimitiveType.I64,
			"u8" => PrimitiveType.U8,
			"u16" => PrimitiveType.U16,
			"u32" => PrimitiveType.U32,
			"u64" => PrimitiveType.U64,
			"f16" => PrimitiveType.F16,
			"f32" => PrimitiveType.F32,
			"f64" => PrimitiveType.F64,
			"bool" => PrimitiveType.Bool,
			"char" => PrimitiveType.Char,
			"str" => PrimitiveType.Str,
			"void" => PrimitiveType.Void,
			"object" => PrimitiveType.Object,
			"inum" => PrimitiveType.Inum,
			"fnum" => PrimitiveType.Fnum,
			"num" => PrimitiveType.Num,
			"never" => PrimitiveType.Never,
			"none" => PrimitiveType.None,
			_ => PrimitiveType.Object,
		};
		return name switch
		{
			"i8" or "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "u64" or
			"f16" or "f32" or "f64" or "bool" or "char" or "str" or "void" or "object" or
			"inum" or "fnum" or "num" or "never" or "none" => true,
			_ => false,
		};
	}
}
