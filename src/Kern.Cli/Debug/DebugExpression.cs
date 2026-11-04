using System.Globalization;
using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Cli.Debug;

/// <summary>
/// Вычислитель отладочных выражений (docs/DEBUGGER.md): литералы, локальные имена,
/// арифметика/сравнения/логика, поля, индексы и вызовы функций/методов. Полноценный
/// Kern-компилятор в контексте кадра недоступен, поэтому <c>match</c>, лямбды и
/// конструкции типов не поддерживаются — выражение должно состоять из уже существующих
/// значений и вызовов.
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
		var evaluator = new Evaluator(text, pause);
		try
		{
			value = evaluator.ParseExpression();
			if (!evaluator.AtEnd)
				throw new FormatException($"лишний текст '{evaluator.Rest}'");
			return true;
		}
		catch (FormatException exception)
		{
			error = exception.Message;
			return false;
		}
		catch (KernPanic exception)
		{
			error = exception.Message;
			return false;
		}
		catch (InvalidOperationException exception)
		{
			error = exception.Message;
			return false;
		}
	}

	private sealed class Evaluator(string text, DebugPauseInfo pause)
	{
		private int _position;

		public bool AtEnd => _position >= text.Length;

		public string Rest => text[_position..].Trim();

		public KValue ParseExpression() => ParseOr();

		private KValue ParseOr()
		{
			KValue left = ParseAnd();
			while (TryTake("||"))
			{
				KValue right = ParseAnd();
				left = KValue.Bool(Truthy(left) || Truthy(right));
			}
			return left;
		}

		private KValue ParseAnd()
		{
			KValue left = ParseEquality();
			while (TryTake("&&"))
			{
				KValue right = ParseEquality();
				left = KValue.Bool(Truthy(left) && Truthy(right));
			}
			return left;
		}

		private KValue ParseEquality()
		{
			KValue left = ParseComparison();
			while (true)
			{
				if (TryTake("=="))
					left = KValue.Bool(Equal(left, ParseComparison()));
				else if (TryTake("!="))
					left = KValue.Bool(!Equal(left, ParseComparison()));
				else
					return left;
			}
		}

		private KValue ParseComparison()
		{
			KValue left = ParseAdditive();
			while (true)
			{
				if (TryTake("<="))
					left = KValue.Bool(Compare(left, ParseAdditive()) <= 0);
				else if (TryTake(">="))
					left = KValue.Bool(Compare(left, ParseAdditive()) >= 0);
				else if (TryTake("<"))
					left = KValue.Bool(Compare(left, ParseAdditive()) < 0);
				else if (TryTake(">"))
					left = KValue.Bool(Compare(left, ParseAdditive()) > 0);
				else
					return left;
			}
		}

		private KValue ParseAdditive()
		{
			KValue left = ParseMultiplicative();
			while (true)
			{
				if (TryTake("+"))
				{
					KValue right = ParseMultiplicative();
					left = left.Kind == ValueKind.Str && right.Kind == ValueKind.Str
						? KValue.Str(left.AsStr + right.AsStr)
						: Numeric(left, right, static (a, b) => a + b, static (a, b) => a + b);
				}
				else if (TryTake("-"))
					left = Numeric(left, ParseMultiplicative(), static (a, b) => a - b, static (a, b) => a - b);
				else
					return left;
			}
		}

		private KValue ParseMultiplicative()
		{
			KValue left = ParseUnary();
			while (true)
			{
				if (TryTake("*"))
					left = Numeric(left, ParseUnary(), static (a, b) => a * b, static (a, b) => a * b);
				else if (TryTake("/"))
					left = Numeric(left, ParseUnary(), Divide, static (a, b) => a / b);
				else if (TryTake("%"))
					left = Numeric(left, ParseUnary(), Modulo, static (a, b) => a % b);
				else
					return left;
			}
		}

		private KValue ParseUnary()
		{
			if (TryTake("!"))
				return KValue.Bool(!Truthy(ParseUnary()));
			if (TryTake("-"))
			{
				KValue value = ParseUnary();
				return IsInteger(value) ? KValue.I64(-Integer(value)) : KValue.F64(-Real(value));
			}
			return ParsePostfix();
		}

		private KValue ParsePostfix()
		{
			KValue value = ParsePrimary();
			while (true)
			{
				if (TryTake("."))
				{
					string name = ParseIdentifier();
					if (name.Length == 0)
						throw new FormatException("ожидалось имя поля или метода после '.'");
					if (TryTake("("))
						value = CallMethod(value, name, ParseArguments());
					else
						value = ReadMember(value, name);
					continue;
				}

				if (TryTake("["))
				{
					KValue index = ParseExpression();
					Expect("]");
					value = Index(value, index);
					continue;
				}

				return value;
			}
		}

		private KValue ParsePrimary()
		{
			SkipSpaces();
			if (TryTake("("))
			{
				KValue value = ParseExpression();
				Expect(")");
				return value;
			}

			if (!AtEnd && text[_position] == '"')
				return KValue.Str(ParseString());
			if (!AtEnd && char.IsDigit(text[_position]))
				return ParseNumber();

			string name = ParseIdentifier();
			if (name.Length == 0)
				throw new FormatException("ожидалось значение или имя");
			if (name is "true" or "false")
				return KValue.Bool(name == "true");
			if (name == "nil")
				return KValue.Nil;
			if (TryTake("("))
				return CallFunction(name, ParseArguments());
			return Lookup(name);
		}

		private List<KValue> ParseArguments()
		{
			var arguments = new List<KValue>();
			if (TryTake(")"))
				return arguments;
			while (true)
			{
				arguments.Add(ParseExpression());
				if (TryTake(")"))
					return arguments;
				Expect(",");
			}
		}

		private KValue Lookup(string name)
		{
			for (var slot = 0; slot < pause.LocalNames.Count; slot++)
				if (string.Equals(pause.LocalNames[slot], name, StringComparison.Ordinal) &&
					slot < pause.Locals.Count)
					return pause.Locals[slot];
			throw new FormatException($"нет локальной переменной '{name}'");
		}

		private KValue CallFunction(string name, List<KValue> arguments)
		{
			Interpreter interpreter = Interpreter();
			foreach (FunctionDef function in interpreter.Module.FunctionPool)
			{
				if (function.OwnerType >= 0 || function.Name != name || function.ParamCount != arguments.Count)
					continue;
				if (function.IsNative)
					throw new FormatException($"вызов нативной функции '{name}' в отладочном выражении не поддержан");
				return interpreter.RunValue(KValue.Obj(function), [.. arguments]);
			}

			throw new FormatException($"нет функции '{name}' с {arguments.Count} аргументами");
		}

		private KValue CallMethod(KValue receiver, string name, List<KValue> arguments)
		{
			if (receiver.Ref is not KObject instance)
			{
				if (name == "to_str" && arguments.Count == 0)
					return KValue.Str(receiver.ToString() ?? "");
				throw new FormatException($"у значения '{receiver.TypeName}' нет метода '{name}'");
			}

			Interpreter interpreter = Interpreter();
			foreach (TypeDef definition in Chain(interpreter, instance.Type.Definition))
			{
				foreach (int index in definition.Methods)
				{
					FunctionDef method = interpreter.Module.FunctionPool[index];
					if (method.Name != name || method.ParamCount != arguments.Count + 1)
						continue;
					if (method.IsNative)
						throw new FormatException($"вызов нативного метода '{name}' не поддержан");
					return interpreter.RunValue(KValue.Obj(method), [receiver, .. arguments]);
				}

				foreach (PropertyDef property in definition.Properties)
				{
					if (property.Name != name || arguments.Count != 0 || property.Getter < 0)
						continue;
					return interpreter.RunValue(
						KValue.Obj(interpreter.Module.FunctionPool[property.Getter]),
						[receiver]);
				}
			}

			throw new FormatException($"у типа '{instance.Type.Canonical}' нет метода '{name}'");
		}

		private KValue ReadMember(KValue value, string name)
		{
			if (value.Kind == ValueKind.Str && name == "len")
				return KValue.I32(value.AsStr.Length);
			if (name == "len")
			{
				return value.Ref switch
				{
					KList list => KValue.I32(list.Count),
					KArray array => KValue.I32(array.Length),
					KDict dict => KValue.I32(dict.Count),
					_ => throw new FormatException($"у значения '{value.TypeName}' нет len"),
				};
			}

			if (value.Ref is not KObject instance)
				throw new FormatException($"у значения '{value.TypeName}' нет поля '{name}'");

			Interpreter interpreter = Interpreter();
			foreach (TypeDef definition in Chain(interpreter, instance.Type.Definition))
			{
				foreach (FieldDef field in definition.Fields)
					if (field.Name == name && (field.Flags & MemberFlags.Static) == 0)
						return instance.GetField(field.Slot);
				foreach (PropertyDef property in definition.Properties)
					if (property.Name == name && property.Getter >= 0)
						return interpreter.RunValue(
							KValue.Obj(interpreter.Module.FunctionPool[property.Getter]),
							[value]);
			}

			throw new FormatException($"у типа '{instance.Type.Canonical}' нет поля или свойства '{name}'");
		}

		private static KValue Index(KValue target, KValue index)
		{
			if (target.Kind == ValueKind.Str)
			{
				int position = (int)Integer(index);
				string text = target.AsStr;
				if (position < 0 || position >= text.Length)
					throw new FormatException($"индекс {position} вне строки");
				return KValue.Char(text[position]);
			}

			return target.Ref switch
			{
				KList list => list[(int)Integer(index)],
				KArray array => array[(int)Integer(index)],
				KDict dict => dict.Get(index),
				_ => throw new FormatException($"значение '{target.TypeName}' не индексируется"),
			};
		}

		private Interpreter Interpreter() =>
			pause.Interpreter ?? throw new FormatException("VM недоступна для вычисления выражения");

		private static IEnumerable<TypeDef> Chain(Interpreter interpreter, TypeDef definition)
		{
			yield return definition;
			foreach (TypeSig parent in definition.Parents)
			{
				if (parent is not TypeSig.NamedSig named)
					continue;
				TypeDef? found = interpreter.Module.TypePool.FirstOrDefault(item => item.FullName == named.FullName);
				if (found is null || ReferenceEquals(found, definition))
					continue;
				foreach (TypeDef ancestor in Chain(interpreter, found))
					yield return ancestor;
			}
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
			SkipSpaces();
			int start = _position;
			if (AtEnd || !(char.IsLetter(text[_position]) || text[_position] is '_' or '$'))
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

		private bool TryTake(string token)
		{
			SkipSpaces();
			if (_position + token.Length > text.Length ||
				!string.Equals(text[_position..(_position + token.Length)], token, StringComparison.Ordinal))
				return false;
			// `<` не должен съесть начало `<=`, `>` — `>=`, `!` — `!=`.
			if (token is "<" or ">" && _position + 1 < text.Length && text[_position + 1] == '=')
				return false;
			if (token == "!" && _position + 1 < text.Length && text[_position + 1] == '=')
				return false;
			_position += token.Length;
			return true;
		}

		private void Expect(string token)
		{
			if (!TryTake(token))
				throw new FormatException($"ожидался '{token}'");
		}

		private static KValue Numeric(
			KValue left,
			KValue right,
			Func<long, long, long?> integers,
			Func<double, double, double> reals)
		{
			if (IsInteger(left) && IsInteger(right))
			{
				long result = integers(Integer(left), Integer(right))
					?? throw new FormatException("деление на ноль");
				return KValue.I64(result);
			}

			return KValue.F64(reals(Real(left), Real(right)));
		}

		private static long? Divide(long left, long right) => right == 0 ? null : left / right;

		private static long? Modulo(long left, long right) => right == 0 ? null : left % right;

		private static bool Equal(KValue left, KValue right)
		{
			if (left.Kind != right.Kind)
				return IsNumeric(left) && IsNumeric(right) && Real(left) == Real(right);
			return left.Kind switch
			{
				ValueKind.Nil or ValueKind.None or ValueKind.Void => true,
				ValueKind.Bool => left.AsBool == right.AsBool,
				ValueKind.Char => left.AsChar == right.AsChar,
				ValueKind.Str => left.AsStr == right.AsStr,
				ValueKind.I8 or ValueKind.I16 or ValueKind.I32 or ValueKind.I64 or
					ValueKind.U8 or ValueKind.U16 or ValueKind.U32 or ValueKind.U64 => Integer(left) == Integer(right),
				ValueKind.F16 or ValueKind.F32 or ValueKind.F64 => Real(left) == Real(right),
				ValueKind.Ref => ReferenceEquals(left.Ref, right.Ref),
				_ => false,
			};
		}

		private static int Compare(KValue left, KValue right)
		{
			if (left.Kind == ValueKind.Str && right.Kind == ValueKind.Str)
				return string.CompareOrdinal(left.AsStr, right.AsStr);
			if (IsNumeric(left) && IsNumeric(right))
				return Real(left).CompareTo(Real(right));
			if (left.Kind == ValueKind.Char && right.Kind == ValueKind.Char)
				return left.AsChar.CompareTo(right.AsChar);
			throw new FormatException($"значения '{left.TypeName}' и '{right.TypeName}' нельзя сравнить");
		}

		private static bool Truthy(KValue value) => value.Kind switch
		{
			ValueKind.Nil or ValueKind.None or ValueKind.Void => false,
			ValueKind.Bool => value.AsBool,
			ValueKind.Str => value.AsStr.Length > 0,
			ValueKind.Ref => value.Ref is not null,
			_ => IsNumeric(value) && Real(value) != 0,
		};

		private static bool IsNumeric(KValue value) => IsInteger(value) ||
			value.Kind is ValueKind.F16 or ValueKind.F32 or ValueKind.F64;

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
