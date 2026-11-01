using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Vm.Async;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Binding.Mapping;

/// <summary>
/// Соответствие CLR-типов Kern-типам (spec §12.4). Имена строятся в текстовом виде
/// (`list&lt;i32&gt;`), потому что этот же текст попадает в <c>NativeKey</c> и в сигнатуры
/// символов компилятора.
/// </summary>
public static class TypeMapping
{
	private static readonly Dictionary<Type, string> Primitives = new()
	{
		[typeof(void)] = "void",
		[typeof(bool)] = "bool",
		[typeof(char)] = "char",
		[typeof(sbyte)] = "i8",
		[typeof(short)] = "i16",
		[typeof(int)] = "i32",
		[typeof(long)] = "i64",
		[typeof(byte)] = "u8",
		[typeof(ushort)] = "u16",
		[typeof(uint)] = "u32",
		[typeof(ulong)] = "u64",
		[typeof(Half)] = "f16",
		[typeof(float)] = "f32",
		[typeof(double)] = "f64",
		[typeof(string)] = "str",
		[typeof(KValue)] = "object",
		[typeof(KObject)] = "object",
		[typeof(KType)] = "type",
		[typeof(KTask)] = "task<void>",
		[typeof(object)] = "object",
	};

	/// <summary>CLR-тип → Kern-имя; для generic-параметров возвращает их имя.</summary>
	public static bool TryToKern(Type type, IReadOnlyList<string>? typeParameters, out string kernName)
	{
		ArgumentNullException.ThrowIfNull(type);
		if (type == typeof(Task))
		{
			kernName = "task<void>";
			return true;
		}

		string? mapped = Map(type, typeParameters);
		kernName = mapped ?? "";
		return mapped is not null;
	}

	public static string ToKern(Type type, IReadOnlyList<string>? typeParameters = null) =>
		TryToKern(type, typeParameters, out string kernName)
			? kernName
			: throw new NotSupportedException($"CLR-тип '{type}' не имеет соответствия в Kern");

	private static string? Map(Type type, IReadOnlyList<string>? typeParameters)
	{
		if (type.IsGenericParameter)
		{
			if (typeParameters is not null && !typeParameters.Contains(type.Name))
				return null;
			return type.Name;
		}

		if (type.IsArray)
		{
			string? element = Map(type.GetElementType()!, typeParameters);
			return element is null ? null : $"array<{element}>";
		}

		Type nullable = Nullable.GetUnderlyingType(type) ?? type;
		if (nullable != type)
		{
			string? underlying = Map(nullable, typeParameters);
			return underlying is null ? null : $"{underlying}?";
		}

		if (Primitives.TryGetValue(type, out string? primitive))
			return primitive;

		if (!type.IsGenericType)
		{
			KernTypeAttribute? native = NativeAttribute(type);
			return native?.Name;
		}

		Type definition = type.GetGenericTypeDefinition();
		Type[] arguments = type.GetGenericArguments();
		if (definition == typeof(KList<>))
			return Wrap("list", arguments, typeParameters);
		if (definition == typeof(KArray<>))
			return Wrap("array", arguments, typeParameters);
		if (definition == typeof(KDict<,>))
			return Wrap("dict", arguments, typeParameters);
		if (definition == typeof(KResult<,>))
			return Wrap("result", arguments, typeParameters);
		if (definition == typeof(KTask<>))
			return Wrap("task", arguments, typeParameters);
		if (definition == typeof(Task<>))
			return Wrap("task", arguments, typeParameters);
		if (definition == typeof(ValueTask<>))
			return Wrap("task", arguments, typeParameters);
		if (definition == typeof(Task))
			return "task<void>";
		if (definition == typeof(KFunc<>))
			return Function(new[] { arguments[0] });
		if (definition == typeof(KFunc<,>))
			return Function(arguments);
		if (definition == typeof(KFunc<,,>))
			return Function(arguments);
		if (definition == typeof(KAction))
			return "fun()->void";
		if (definition == typeof(KAction<>))
			return Function(arguments);
		if (definition == typeof(KAction<,>))
			return Function(arguments);
		return null;

		string? Function(Type[] components)
		{
			bool isAction = definition == typeof(KAction) ||
				definition == typeof(KAction<>) ||
				definition == typeof(KAction<,>);
			int parameterCount = isAction ? components.Length : components.Length - 1;
			var parameters = new string[parameterCount];
			for (var index = 0; index < parameterCount; index++)
			{
				string? parameter = Map(components[index], typeParameters);
				if (parameter is null)
					return null;
				parameters[index] = parameter;
			}

			string result = "void";
			if (!isAction)
			{
				string? mappedResult = Map(components[^1], typeParameters);
				if (mappedResult is null)
					return null;
				result = mappedResult;
			}

			return $"fun({string.Join(",", parameters)})->{result}";
		}
	}

	private static string? Wrap(string name, Type[] components, IReadOnlyList<string>? typeParameters)
	{
		var mapped = new string[components.Length];
		for (var index = 0; index < components.Length; index++)
		{
			string? item = Map(components[index], typeParameters);
			if (item is null)
				return null;
			mapped[index] = item;
		}

		return $"{name}<{string.Join(",", mapped)}>";
	}

	/// <summary>Есть ли у типа атрибут <see cref="KernTypeAttribute"/> (нативная привязка).</summary>
	public static KernTypeAttribute? NativeAttribute(Type type) =>
		type.GetCustomAttributes(typeof(KernTypeAttribute), inherit: false)
			.OfType<KernTypeAttribute>()
			.FirstOrDefault();
}
