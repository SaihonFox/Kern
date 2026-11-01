namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Статические сигнатуры нативных членов встроенных generic-типов (spec §12, §16).
/// Мост C# описывает <c>list</c>/<c>array</c>/<c>dict</c> через <see cref="PrimitiveType.Object"/>
/// (в VM это <c>KValue</c>), поэтому binder подставляет тип результата по аргументам
/// получателя: <c>list&lt;i32&gt;.first() -&gt; i32</c>, <c>dict&lt;str, i32&gt;.get(...) -&gt; i32</c>.
/// Runtime не меняется: <c>KValue</c> уже несёт свой вид.
/// </summary>
internal static class BuiltinMemberSignatures
{
	/// <summary>
	/// Возвращает тип результата нативного члена или <c>null</c>, если таблица член не
	/// описывает (тогда остаётся сигнатура моста). <paramref name="error"/> заполняется
	/// русским текстом для <c>KRN2102</c>, когда аргумент <c>map</c>/<c>filter</c> неверной формы.
	/// </summary>
	public static KernType? ResolveReturnType(
		BuiltinGenericType receiver,
		string member,
		IReadOnlyList<KernType> argumentTypes,
		out string? error)
	{
		error = null;
		return receiver.BuiltinKind switch
		{
			BuiltinGenericKind.List when receiver.Arguments.Count == 1 =>
				ResolveSequence(receiver, receiver.Arguments[0], member, argumentTypes, isArray: false, out error),
			BuiltinGenericKind.Array when receiver.Arguments.Count == 1 =>
				ResolveSequence(receiver, receiver.Arguments[0], member, argumentTypes, isArray: true, out error),
			BuiltinGenericKind.Dict when receiver.Arguments.Count == 2 =>
				ResolveDict(receiver.Arguments[0], receiver.Arguments[1], member, out error),
			BuiltinGenericKind.Set when receiver.Arguments.Count == 1 =>
				ResolveSet(receiver.Arguments[0], member, out error),
			BuiltinGenericKind.Queue when receiver.Arguments.Count == 1 =>
				ResolveQueue(receiver.Arguments[0], member, out error),
			BuiltinGenericKind.Stack when receiver.Arguments.Count == 1 =>
				ResolveStack(receiver.Arguments[0], member, out error),
			BuiltinGenericKind.Collection when receiver.Arguments.Count == 1 =>
				ResolveCollection(receiver.Arguments[0], member, out error),
			BuiltinGenericKind.Observable when receiver.Arguments.Count == 1 =>
				ResolveObservable(receiver.Arguments[0], member, out error),
			_ => null
		};
	}

	/// <summary>
	/// Тип параметра нативного члена, который мост описывает как <c>object</c>, но по смыслу
	/// это элемент контейнера: <c>set&lt;T&gt;.add/remove/contains(T)</c>,
	/// <c>queue&lt;T&gt;.enqueue(T)</c>, <c>stack&lt;T&gt;.push(T)</c>,
	/// <c>collection&lt;T&gt;.contains(T)</c>, <c>observable&lt;T&gt;.add/remove/contains(T)</c>.
	/// </summary>
	public static KernType? ElementParameterType(BuiltinGenericType receiver, string member)
	{
		if (receiver.Arguments.Count != 1)
			return null;

		return receiver.BuiltinKind switch
		{
			BuiltinGenericKind.Set => member is "add" or "remove" or "contains" ? receiver.Arguments[0] : null,
			BuiltinGenericKind.Queue => member is "enqueue" ? receiver.Arguments[0] : null,
			BuiltinGenericKind.Stack => member is "push" ? receiver.Arguments[0] : null,
			BuiltinGenericKind.Collection => member is "contains" ? receiver.Arguments[0] : null,
			BuiltinGenericKind.Observable => member is "add" or "remove" or "contains" ? receiver.Arguments[0] : null,
			_ => null,
		};
	}

	/// <summary>
	/// <c>collection&lt;T&gt;</c>: <c>to_list() -&gt; list&lt;T&gt;</c>; <c>len</c>/<c>is_empty</c>
	/// и <c>contains</c> описываются мостом.
	/// </summary>
	private static KernType? ResolveCollection(KernType element, string member, out string? error)
	{
		error = null;
		return member == "to_list"
			? new BuiltinGenericType(BuiltinGenericKind.List, [element])
			: null;
	}

	/// <summary>
	/// <c>observable&lt;T&gt;</c>: <c>to_list() -&gt; list&lt;T&gt;</c>; <c>on_change</c>
	/// и <c>add/remove/contains</c> описываются мостом (void/bool).
	/// </summary>
	private static KernType? ResolveObservable(KernType element, string member, out string? error)
	{
		error = null;
		return member == "to_list"
			? new BuiltinGenericType(BuiltinGenericKind.List, [element])
			: null;
	}

	private static KernType? ResolveSequence(
		BuiltinGenericType receiver,
		KernType element,
		string member,
		IReadOnlyList<KernType> argumentTypes,
		bool isArray,
		out string? error)
	{
		error = null;
		switch (member)
		{
			// Элемент возвращается как есть: `first`/`last`/`get`/`remove_at`.
			case "first":
			case "last":
			case "get":
			case "remove_at":
				return element;

			case "reverse":
				return receiver;

			// Runtime `ArrayApi.Slice` возвращает новый `list<T>` (как и срез `xs[a..b]`),
			// поэтому у массива результат именно `list<T>`, а не `array<T>`.
			case "slice":
				return isArray ? new BuiltinGenericType(BuiltinGenericKind.List, [element]) : receiver;

			case "filter":
				if (!TryGetSingleFunction(member, argumentTypes, out FunctionType filter, out error))
					return null;
				if (filter.ReturnType is not PrimitiveType { Primitive: PrimitiveKind.Bool })
				{
					error = $"Функция 'filter' должна возвращать 'bool', получено '{filter.ReturnType}'";
					return null;
				}

				return receiver;

			case "map":
				if (!TryGetSingleFunction(member, argumentTypes, out FunctionType mapper, out error))
					return null;
				return new BuiltinGenericType(BuiltinGenericKind.List, [mapper.ReturnType]);

			// join/contains/index_of/add/push/set/clear/len/is_empty — сигнатура моста верна.
			default:
				return null;
		}
	}

	/// <summary>
	/// <c>set&lt;T&gt;</c>: <c>to_list() -&gt; list&lt;T&gt;</c>; <c>len</c>/<c>is_empty</c>
	/// и предикаты <c>add/remove/contains</c> описываются мостом (int/bool/void).
	/// </summary>
	private static KernType? ResolveSet(KernType element, string member, out string? error)
	{
		error = null;
		return member switch
		{
			"to_list" => new BuiltinGenericType(BuiltinGenericKind.List, [element]),
			_ => null
		};
	}

	/// <summary>
	/// <c>queue&lt;T&gt;</c>: <c>dequeue()</c>/<c>peek() -&gt; T</c>,
	/// <c>to_list() -&gt; list&lt;T&gt;</c>; <c>len</c>/<c>is_empty</c>/<c>clear</c>
	/// описываются мостом (int/bool/void).
	/// </summary>
	private static KernType? ResolveQueue(KernType element, string member, out string? error)
	{
		error = null;
		return member switch
		{
			"dequeue" or "peek" => element,
			"to_list" => new BuiltinGenericType(BuiltinGenericKind.List, [element]),
			_ => null
		};
	}

	/// <summary>
	/// <c>stack&lt;T&gt;</c>: <c>pop()</c>/<c>peek() -&gt; T</c>,
	/// <c>to_list() -&gt; list&lt;T&gt;</c>; <c>len</c>/<c>is_empty</c>/<c>clear</c>
	/// описываются мостом (int/bool/void).
	/// </summary>
	private static KernType? ResolveStack(KernType element, string member, out string? error)
	{
		error = null;
		return member switch
		{
			"pop" or "peek" => element,
			"to_list" => new BuiltinGenericType(BuiltinGenericKind.List, [element]),
			_ => null
		};
	}

	private static KernType? ResolveDict(
		KernType key,
		KernType value,
		string member,
		out string? error)
	{
		error = null;
		return member switch
		{
			"get" => value,
			"get_or" => value,
			"keys" => new BuiltinGenericType(BuiltinGenericKind.List, [key]),
			"values" => new BuiltinGenericType(BuiltinGenericKind.List, [value]),
			// contains_key/remove/len/is_empty/set/clear — сигнатура моста верна.
			_ => null
		};
	}

	/// <summary>
	/// <c>map</c>/<c>filter</c> принимают ровно одну функцию с ровно одним параметром.
	/// Проверка намеренно консервативная: форма аргумента, а не совместимость типа параметра.
	/// </summary>
	private static bool TryGetSingleFunction(
		string member,
		IReadOnlyList<KernType> argumentTypes,
		out FunctionType function,
		out string? error)
	{
		if (argumentTypes.Count == 1 && argumentTypes[0] is FunctionType { Parameters.Count: 1 } found)
		{
			function = found;
			error = null;
			return true;
		}

		function = null!;
		string actual = argumentTypes.Count == 1 ? $"'{argumentTypes[0]}'" : $"{argumentTypes.Count} аргумента(ов)";
		error = $"Аргумент '{member}' должен быть функцией с одним параметром, получено {actual}";
		return false;
	}
}
