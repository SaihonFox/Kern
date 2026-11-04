using System.Globalization;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Cli.Debug;

/// <summary>
/// Мини-вычислитель для команды <c>print</c> (docs/DEBUGGER.md): литералы, имена
/// локальных переменных, скобки и арифметика <c>+ - * / %</c> (плюс конкатенация строк).
/// Полноценные Kern-выражения (вызовы, поля, индексы) не поддерживаются — для них нужен
/// компилятор в контексте кадра.
/// </summary>
internal static class DebugExpression
{
	public static bool TryEvaluate(
		string text,
		DebugPauseInfo pause,
		out KValue value,
		out string? error)
	{
		value = KValue.Nil;
		error = null;
		var parser = new Parser(text, pause);
		try
		{
			value = parser.ParseExpression();
			if (!parser.AtEnd)
				throw new FormatException($"лишний текст '{parser.Rest}'");
			return true;
		}
		catch (FormatException exception)
		{
			error = exception.Message;
			return false;
		}
	}

	private sealed class Parser(string text, DebugPauseInfo pause)
	{
		private int _position;

		public bool AtEnd => _position >= text.Length;

		public string Rest => text[_position..].Trim();

		public KValue ParseExpression()
		{
			KValue left = ParseTerm();
			while (true)
			{
				SkipSpaces();
				if (!TryTake('+') && !TryTake('-'))
					return left;
				bool plus = text[_position - 1] == '+';
				KValue right = ParseTerm();
				left = AddOrSubtract(left, right, plus);
			}
		}

		private KValue ParseTerm()
		{
			KValue left = ParseFactor();
			while (true)
			{
				SkipSpaces();
				if (!TryTake('*') && !TryTake('/') && !TryTake('%'))
					return left;
				char op = text[_position - 1];
				KValue right = ParseFactor();
				left = Multiply(left, right, op);
			}
		}

		private KValue ParseFactor()
		{
			SkipSpaces();
			if (TryTake('-'))
				return Negate(ParseFactor());
			if (TryTake('('))
			{
				KValue value = ParseExpression();
				SkipSpaces();
				if (!TryTake(')'))
					throw new FormatException("не закрыта скобка");
				return value;
			}

			if (!AtEnd && text[_position] == '"')
				return KValue.Str(ParseString());
			if (!AtEnd && char.IsDigit(text[_position]))
				return ParseNumber();

			string name = ParseIdentifier();
			if (name.Length == 0)
				throw new FormatException("ожидалось значение или имя локальной");
			return name switch
			{
				"true" => KValue.Bool(true),
				"false" => KValue.Bool(false),
				"nil" => KValue.Nil,
				_ => Lookup(name),
			};
		}

		private KValue Lookup(string name)
		{
			for (var slot = 0; slot < pause.LocalNames.Count; slot++)
				if (string.Equals(pause.LocalNames[slot], name, StringComparison.Ordinal) &&
					slot < pause.Locals.Count)
					return pause.Locals[slot];
			throw new FormatException($"нет локальной переменной '{name}'");
		}

		private KValue ParseNumber()
		{
			int start = _position;
			while (!AtEnd && (
				char.IsDigit(text[_position]) ||
				text[_position] is '.' or '_' or 'e' or 'E' ||
				((text[_position] is '+' or '-') && _position > start && text[_position - 1] is 'e' or 'E')))
			{
				_position++;
			}

			string raw = text[start.._position].Replace("_", "", StringComparison.Ordinal);
			if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E'))
			{
				if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double real))
					throw new FormatException($"некорректное число '{raw}'");
				return KValue.F64(real);
			}

			if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
				throw new FormatException($"некорректное число '{raw}'");
			return KValue.I64(integer);
		}

		private string ParseString()
		{
			_position++;
			var builder = new System.Text.StringBuilder();
			while (!AtEnd)
			{
				char symbol = text[_position++];
				if (symbol == '"')
					return builder.ToString();
				if (symbol == '\\' && !AtEnd)
				{
					char escaped = text[_position++];
					builder.Append(escaped switch
					{
						'n' => '\n',
						't' => '\t',
						'r' => '\r',
						_ => escaped,
					});
					continue;
				}
				builder.Append(symbol);
			}

			throw new FormatException("не закрыта строка");
		}

		private string ParseIdentifier()
		{
			int start = _position;
			if (AtEnd || !(char.IsLetter(text[_position]) || text[_position] == '_' || text[_position] == '$'))
				return "";
			_position++;
			while (!AtEnd && (char.IsLetterOrDigit(text[_position]) || text[_position] is '_' or '$'))
				_position++;
			return text[start.._position];
		}

		private void SkipSpaces()
		{
			while (!AtEnd && char.IsWhiteSpace(text[_position]))
				_position++;
		}

		private bool TryTake(char symbol)
		{
			SkipSpaces();
			if (AtEnd || text[_position] != symbol)
				return false;
			_position++;
			return true;
		}

		private static KValue Negate(KValue value) =>
			IsInteger(value) ? KValue.I64(-Integer(value)) : KValue.F64(-Real(value));

		private static KValue AddOrSubtract(KValue left, KValue right, bool plus)
		{
			if (plus && left.Kind == ValueKind.Str && right.Kind == ValueKind.Str)
				return KValue.Str(left.AsStr + right.AsStr);
			if (IsInteger(left) && IsInteger(right))
				return KValue.I64(plus ? Integer(left) + Integer(right) : Integer(left) - Integer(right));
			return KValue.F64(plus ? Real(left) + Real(right) : Real(left) - Real(right));
		}

		private static KValue Multiply(KValue left, KValue right, char op)
		{
			if (IsInteger(left) && IsInteger(right))
			{
				long divisor = Integer(right);
				return op switch
				{
					'*' => KValue.I64(Integer(left) * divisor),
					'/' when divisor != 0 => KValue.I64(Integer(left) / divisor),
					'%' when divisor != 0 => KValue.I64(Integer(left) % divisor),
					_ => throw new FormatException("деление на ноль"),
				};
			}

			double leftReal = Real(left);
			double rightReal = Real(right);
			return op switch
			{
				'*' => KValue.F64(leftReal * rightReal),
				'/' => KValue.F64(leftReal / rightReal),
				_ => KValue.F64(leftReal % rightReal),
			};
		}

		private static bool IsInteger(KValue value) =>
			value.Kind is ValueKind.I8 or ValueKind.I16 or ValueKind.I32 or ValueKind.I64
				or ValueKind.U8 or ValueKind.U16 or ValueKind.U32 or ValueKind.U64;

		private static long Integer(KValue value) => value.Kind switch
		{
			ValueKind.I8 => value.AsI8,
			ValueKind.I16 => value.AsI16,
			ValueKind.I32 => value.AsI32,
			ValueKind.I64 => value.AsI64,
			ValueKind.U8 => value.AsU8,
			ValueKind.U16 => value.AsU16,
			ValueKind.U32 => value.AsU32,
			ValueKind.U64 => (long)value.AsU64,
			_ => throw new FormatException($"ожидалось целое, получено {value.TypeName}"),
		};

		private static double Real(KValue value) => value.Kind switch
		{
			ValueKind.F16 => (double)value.AsF16,
			ValueKind.F32 => value.AsF32,
			ValueKind.F64 => value.AsF64,
			_ => IsInteger(value) ? Integer(value) : throw new FormatException($"ожидалось число, получено {value.TypeName}"),
		};
	}
}
