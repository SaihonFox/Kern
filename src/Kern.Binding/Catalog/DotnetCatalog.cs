using System.Collections.Concurrent;
using System.Reflection;
using Kern.Binding.Mapping;
using Kern.Vm.Native;

namespace Kern.Binding.Catalog;

/// <summary>
/// Импорт .NET-типов через <c>use dotnet.Namespace[T1, T2]</c> (spec §12.10, «способ C»).
/// Статический класс даёт статические методы/свойства; обычный класс — конструкторы и
/// экземплярные методы/свойства (приёмник передаётся первым аргументом). Имена членов,
/// параметров и типов сохраняются как в C# (<c>builder.Length</c>, <c>File.ReadAllText</c>).
/// Открытые generic-типы (<c>List</c>, <c>Dictionary</c>) импортируются с параметрами
/// типа: <c>List&lt;i32&gt;</c>, <c>Dictionary&lt;str,i32&gt;</c>.
/// </summary>
public static class DotnetCatalog
{
	/// <summary>
	/// Простые Kern-имена импортированных CLR-типов: нужны, чтобы закрыть
	/// generic-аргумент <c>List&lt;StringBuilder&gt;</c> на стороне VM.
	/// </summary>
	private static readonly ConcurrentDictionary<string, Type> ImportedTypes = new(StringComparer.Ordinal);

	/// <summary>CLR-определения импортированных типов: отличают handle от перечисления в Kern.</summary>
	private static readonly ConcurrentDictionary<Type, byte> ImportedClr = new();

	public static NativeCatalog Import(NativeCatalog catalog, IEnumerable<Type> types)
	{
		ArgumentNullException.ThrowIfNull(catalog);
		ArgumentNullException.ThrowIfNull(types);

		var extraTypes = new List<NativeTypeDescriptor>();
		var extraFunctions = new List<NativeFunctionDescriptor>();

		foreach (Type type in types.Distinct())
		{
			bool openGeneric = type.IsGenericTypeDefinition;
			if (!openGeneric && type.Name.Contains('`', StringComparison.Ordinal))
				continue;

			string typeName = SimpleName(type);
			IReadOnlyList<string>? typeParameters = openGeneric
				? [.. type.GetGenericArguments().Select(argument => argument.Name)]
				: null;

			var members = new List<NativeFunctionDescriptor>();
			if (type.IsAbstract && type.IsSealed)
				ImportStatic(type, typeName, typeParameters, members);
			else
			{
				ImportInstance(type, typeName, typeParameters, members);
				// Обычные классы тоже имеют статические члены: `String.Join`, `Int32.Parse`.
				ImportStatic(type, typeName, typeParameters, members);
			}

			if (members.Count == 0)
				continue;

			ImportedTypes[typeName] = type;
			ImportedClr[type] = 0;
			extraTypes.Add(new NativeTypeDescriptor
			{
				Name = typeName,
				ClrType = type,
				GenericArity = typeParameters?.Count ?? 0,
				GenericParameterNames = typeParameters ?? [],
				Members = members,
			});
			extraFunctions.AddRange(members);
		}

		return catalog.Extend(extraTypes, extraFunctions);
	}

	/// <summary>CLR-тип по Kern-имени, если он был импортирован через <c>use dotnet</c>.</summary>
	public static Type? FindImportedType(string kernName) =>
		ImportedTypes.TryGetValue(kernName, out Type? type) ? type : null;

	/// <summary>
	/// Тип (или его открытое определение) импортирован через <c>use dotnet</c>: его значения
	/// возвращаются как нативные handle-объекты, а не разворачиваются в Kern-коллекции.
	/// </summary>
	public static bool IsImportedClr(Type type)
	{
		ArgumentNullException.ThrowIfNull(type);
		Type definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
		return ImportedClr.ContainsKey(definition);
	}

	private static void ImportStatic(
		Type type,
		string typeName,
		IReadOnlyList<string>? typeParameters,
		List<NativeFunctionDescriptor> members)
	{
		foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
		{
			if (method.IsSpecialName || method.IsGenericMethodDefinition || method.DeclaringType == typeof(object))
				continue;
			if (TryBuild(method, type, typeName, typeParameters, NativeMemberKind.Method, out NativeFunctionDescriptor? descriptor))
				members.Add(descriptor!);
		}

		foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Static))
		{
			MethodInfo? getter = property.GetMethod;
			if (getter is null || !getter.IsPublic || getter.IsGenericMethodDefinition)
				continue;
			if (TryBuild(getter, type, typeName, typeParameters, NativeMemberKind.PropertyGet, out NativeFunctionDescriptor? descriptor))
				members.Add(descriptor!);
		}
	}

	private static void ImportInstance(
		Type type,
		string typeName,
		IReadOnlyList<string>? typeParameters,
		List<NativeFunctionDescriptor> members)
	{
		foreach (ConstructorInfo constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
		{
			if (TryBuildConstructor(constructor, type, typeName, typeParameters, out NativeFunctionDescriptor? descriptor))
				members.Add(descriptor!);
		}

		foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
		{
			if (method.IsSpecialName || method.IsGenericMethodDefinition || method.DeclaringType == typeof(object))
				continue;
			if (TryBuildInstance(method, type, typeName, typeParameters, NativeMemberKind.Method, out NativeFunctionDescriptor? descriptor))
				members.Add(descriptor!);
		}

		foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			MethodInfo? getter = property.GetMethod;
			if (getter is not null && getter.IsPublic && !getter.IsGenericMethodDefinition &&
				TryBuildInstance(getter, type, typeName, typeParameters, NativeMemberKind.PropertyGet, out NativeFunctionDescriptor? read))
				members.Add(read!);
			MethodInfo? setter = property.SetMethod;
			if (setter is not null && setter.IsPublic && !setter.IsGenericMethodDefinition &&
				TryBuildInstance(setter, type, typeName, typeParameters, NativeMemberKind.PropertySet, out NativeFunctionDescriptor? write))
				members.Add(write!);
		}
	}

	/// <summary>
	/// Статический член: без приёмника, ключ <c>dotnet:Type|name|kind|t|params</c>.
	/// </summary>
	internal static bool TryBuild(
		MethodInfo method,
		Type owner,
		string ownerName,
		IReadOnlyList<string>? typeParameters,
		NativeMemberKind kind,
		out NativeFunctionDescriptor? descriptor)
	{
		descriptor = null;
		if (!TryMap(method.ReturnType, ownerName, typeParameters, out string returnType))
			return false;
		if (!TryMapParameters(method, ownerName, typeParameters, out List<NativeParameterDescriptor>? mapped, out List<string>? keyTypes))
			return false;

		string name = MemberName(method, kind);
		string key = NativeKey.Create("dotnet:" + owner.FullName, name, kind, isStatic: true, keyTypes);
		descriptor = new NativeFunctionDescriptor
		{
			Name = name,
			Owner = ownerName,
			OwnerIsModule = false,
			Kind = kind,
			IsStatic = true,
			IsStaticMember = true,
			ReturnType = returnType,
			Parameters = mapped,
			KeyTypes = keyTypes,
			GenericParameters = typeParameters ?? [],
			NativeKey = key,
			Method = method,
		};
		return true;
	}

	/// <summary>Экземплярный член: приёмник — первый аргумент, ключ включает self-тип.</summary>
	internal static bool TryBuildInstance(
		MethodInfo method,
		Type owner,
		string ownerName,
		IReadOnlyList<string>? typeParameters,
		NativeMemberKind kind,
		out NativeFunctionDescriptor? descriptor)
	{
		descriptor = null;
		if (!TryMap(method.ReturnType, ownerName, typeParameters, out string returnType))
			return false;
		if (!TryMapParameters(method, ownerName, typeParameters, out List<NativeParameterDescriptor>? mapped, out List<string>? parameterTypes))
			return false;

		string name = MemberName(method, kind);
		string selfType = SelfType(ownerName, typeParameters);
		string selfKey = NativeCatalog.EraseGenerics(selfType, typeParameters ?? []);
		var keyTypes = new List<string>(parameterTypes.Count + 1) { selfKey };
		keyTypes.AddRange(parameterTypes);
		string key = NativeKey.Create("dotnet:" + owner.FullName, name, kind, isStatic: true, keyTypes);
		descriptor = new NativeFunctionDescriptor
		{
			Name = name,
			Owner = ownerName,
			OwnerIsModule = false,
			Kind = kind,
			IsStatic = false,
			ReturnType = returnType,
			SelfType = selfType,
			SelfClrType = typeParameters is null ? owner : null,
			Parameters = mapped,
			KeyTypes = keyTypes,
			GenericParameters = typeParameters ?? [],
			NativeKey = key,
			Method = method,
		};
		return true;
	}

	/// <summary>Конструктор обычного класса: targetless factory, создаёт экземпляр.</summary>
	internal static bool TryBuildConstructor(
		ConstructorInfo constructor,
		Type owner,
		string ownerName,
		IReadOnlyList<string>? typeParameters,
		out NativeFunctionDescriptor? descriptor)
	{
		descriptor = null;
		if (!TryMapConstructorParameters(constructor, ownerName, typeParameters, out List<NativeParameterDescriptor>? mapped, out List<string>? keyTypes))
			return false;

		string name = ownerName;
		string key = NativeKey.Create("dotnet:" + owner.FullName, name, NativeMemberKind.Constructor,
			isStatic: true, keyTypes);
		descriptor = new NativeFunctionDescriptor
		{
			Name = name,
			Owner = ownerName,
			OwnerIsModule = false,
			Kind = NativeMemberKind.Constructor,
			IsStatic = true,
			ReturnType = SelfType(ownerName, typeParameters),
			Parameters = mapped,
			KeyTypes = keyTypes,
			GenericParameters = typeParameters ?? [],
			NativeKey = key,
			Constructor = constructor,
		};
		return true;
	}

	/// <summary>Self-тип члена: <c>List</c> либо <c>List&lt;T&gt;</c> у открытого generic-типа.</summary>
	private static string SelfType(string ownerName, IReadOnlyList<string>? typeParameters) =>
		typeParameters is null || typeParameters.Count == 0
			? ownerName
			: $"{ownerName}<{string.Join(",", typeParameters)}>";

	private static bool TryMapParameters(
		MethodInfo method,
		string? ownerName,
		IReadOnlyList<string>? typeParameters,
		out List<NativeParameterDescriptor> mapped,
		out List<string> keyTypes)
	{
		mapped = [];
		keyTypes = [];
		foreach (ParameterInfo parameter in method.GetParameters())
		{
			if (!TryMapParameter(parameter, ownerName, typeParameters, out NativeParameterDescriptor? descriptor, out string keyType))
				return false;
			mapped.Add(descriptor);
			keyTypes.Add(keyType);
		}

		return true;
	}

	private static bool TryMapConstructorParameters(
		ConstructorInfo constructor,
		string? ownerName,
		IReadOnlyList<string>? typeParameters,
		out List<NativeParameterDescriptor> mapped,
		out List<string> keyTypes)
	{
		mapped = [];
		keyTypes = [];
		foreach (ParameterInfo parameter in constructor.GetParameters())
		{
			if (!TryMapParameter(parameter, ownerName, typeParameters, out NativeParameterDescriptor? descriptor, out string keyType))
				return false;
			mapped.Add(descriptor);
			keyTypes.Add(keyType);
		}

		return true;
	}

	private static bool TryMapParameter(
		ParameterInfo parameter,
		string? ownerName,
		IReadOnlyList<string>? typeParameters,
		out NativeParameterDescriptor descriptor,
		out string keyType)
	{
		descriptor = null!;
		keyType = "";
		bool byRef = parameter.ParameterType.IsByRef;
		Type type = byRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
		if (type.IsPointer || type == typeof(void))
			return false;
		if (!TryMap(type, ownerName, typeParameters, out string typeName))
			return false;

		// CLR `params T[]` — единственный параметр, допускающий расширенную форму.
		bool vararg = !byRef && parameter.GetCustomAttribute<ParamArrayAttribute>() is not null;
		descriptor = new NativeParameterDescriptor(
			parameter.Name ?? $"arg{keyType}",
			typeName,
			byRef,
			vararg);
		keyType = NativeCatalog.EraseGenerics(typeName, typeParameters ?? []);
		return true;
	}

	/// <summary>
	/// Имя члена как в C#; у accessor'ов снимается префикс <c>get_</c>/<c>set_</c>,
	/// чтобы свойство называлось <c>Capacity</c>, а не <c>set_Capacity</c>.
	/// </summary>
	private static string MemberName(MethodInfo method, NativeMemberKind kind)
	{
		if (kind is NativeMemberKind.PropertyGet or NativeMemberKind.PropertySet)
		{
			if (method.Name.StartsWith("get_", StringComparison.Ordinal) ||
				method.Name.StartsWith("set_", StringComparison.Ordinal))
				return method.Name[4..];
		}
		return method.Name;
	}

	/// <summary>
	/// CLR-тип → Kern-имя для <c>use dotnet</c>: известные типы через <see cref="TypeMapping"/>,
	/// остальные негенерические классы/структуры — по простому имени (тип импортируется тем же
	/// <c>use dotnet</c>). Параметры открытого generic-типа сохраняются (<c>T</c>), сам
	/// открытый тип — как <c>List&lt;T&gt;</c>. Массивы неизвестных элементов отклоняются.
	/// </summary>
	private static bool TryMap(Type type, string? ownerName, IReadOnlyList<string>? typeParameters, out string name)
	{
		if (type.IsGenericParameter)
		{
			if (typeParameters is not null && typeParameters.Contains(type.Name, StringComparer.Ordinal))
			{
				name = type.Name;
				return true;
			}

			name = "";
			return false;
		}

		Type nullable = Nullable.GetUnderlyingType(type) ?? type;
		if (nullable != type)
		{
			if (!TryMap(nullable, ownerName, typeParameters, out string underlying))
			{
				name = "";
				return false;
			}
			name = underlying + "?";
			return true;
		}

		if (type.IsArray)
		{
			if (!TryMap(type.GetElementType()!, ownerName, typeParameters, out string element))
			{
				name = "";
				return false;
			}
			name = $"array<{element}>";
			return true;
		}

		if (TypeMapping.TryToKern(type, typeParameters, out name))
			return true;

		// Делегаты BCL (`Action<T>`, `Func<T,R>`, `Predicate<T>`, ...) — Kern-тип функции.
		if (typeof(Delegate).IsAssignableFrom(type) && TryMapDelegate(type, ownerName, typeParameters, out name))
			return true;

		// Открытый generic-тип, который импортируется этим же `use dotnet` (`List<T>`).
		if (ownerName is not null &&
			type.IsGenericType && !type.IsGenericTypeDefinition &&
			type.GetGenericTypeDefinition().Name.StartsWith(ownerName + "`", StringComparison.Ordinal))
		{
			Type[] arguments = type.GetGenericArguments();
			var mapped = new string[arguments.Length];
			for (var index = 0; index < arguments.Length; index++)
			{
				if (!TryMap(arguments[index], ownerName, typeParameters, out mapped[index]))
					return false;
			}

			name = $"{ownerName}<{string.Join(",", mapped)}>";
			return true;
		}

		if (!type.IsGenericType && !type.IsGenericParameter &&
			(type.IsClass || type.IsValueType) && type != typeof(void) && type.Name == ownerName)
		{
			name = type.Name;
			return true;
		}

		name = "";
		return false;
	}

	/// <summary>
	/// Делегат BCL → Kern-тип функции: <c>Action&lt;T&gt;</c> → <c>fun(T)-&gt;void</c>,
	/// <c>Func&lt;T,R&gt;</c> → <c>fun(T)-&gt;R</c>, <c>Predicate&lt;T&gt;</c> →
	/// <c>fun(T)-&gt;bool</c>, <c>Comparison&lt;T&gt;</c> → <c>fun(T,T)-&gt;i32</c>,
	/// <c>Converter&lt;A,B&gt;</c> → <c>fun(A)-&gt;B</c>.
	/// </summary>
	private static bool TryMapDelegate(
		Type type,
		string? ownerName,
		IReadOnlyList<string>? typeParameters,
		out string name)
	{
		name = "";
		Type definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
		Type[] arguments = type.IsGenericType ? type.GetGenericArguments() : [];

		int[] parameterIndexes;
		string result;
		switch (SimpleName(definition))
		{
			case "Action" when arguments.Length <= 4:
				parameterIndexes = [.. Enumerable.Range(0, arguments.Length)];
				result = "void";
				break;
			case "Func" when arguments.Length >= 1 && arguments.Length <= 5:
				parameterIndexes = [.. Enumerable.Range(0, arguments.Length - 1)];
				result = "";
				break;
			case "Predicate" when arguments.Length == 1:
				parameterIndexes = [0];
				result = "bool";
				break;
			case "Comparison" when arguments.Length == 1:
				parameterIndexes = [0, 0];
				result = "i32";
				break;
			case "Converter" when arguments.Length == 2:
				parameterIndexes = [0];
				result = "";
				break;
			default:
				return false;
		}

		if (result.Length == 0)
		{
			if (!TryMap(arguments[^1], ownerName, typeParameters, out result))
				return false;
		}

		var parameters = new string[parameterIndexes.Length];
		for (var index = 0; index < parameterIndexes.Length; index++)
		{
			if (!TryMap(arguments[parameterIndexes[index]], ownerName, typeParameters, out parameters[index]))
				return false;
		}

		name = $"fun({string.Join(",", parameters)})->{result}";
		return true;
	}

	/// <summary>Имя типа без CLR-арности (<c>List`1</c> → <c>List</c>).</summary>
	private static string SimpleName(Type type)
	{
		string name = type.Name;
		int separator = name.IndexOf('`');
		return separator < 0 ? name : name[..separator];
	}

	/// <summary>Kern-имя CLR-типа для <c>use dotnet</c> (без арности).</summary>
	public static string SimpleTypeName(Type type)
	{
		ArgumentNullException.ThrowIfNull(type);
		return SimpleName(type);
	}

	/// <summary>Найти .NET-тип по полному имени (в загруженных сборках и известных BCL).</summary>
	public static Type? FindType(string fullName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
		Type? direct = Type.GetType(fullName, throwOnError: false);
		if (direct is not null)
			return direct;

		// `use dotnet.System.Collections.Generic[List]` не знает арности: пробуем `List`1..
		if (!fullName.Contains('`', StringComparison.Ordinal))
		{
			for (var arity = 1; arity <= 4; arity++)
			{
				Type? generic = Type.GetType($"{fullName}`{arity}", throwOnError: false);
				if (generic is not null)
					return generic;
			}
		}

		foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			Type? found = assembly.GetType(fullName, throwOnError: false);
			if (found is not null)
				return found;
		}

		// Часть BCL живёт в отдельных сборках; пробуем типовые имена.
		foreach (string name in new[] { "System.Runtime", "System.Console", "System.IO.FileSystem", "System.Text.RegularExpressions" })
		{
			try
			{
				Assembly assembly = Assembly.Load(new AssemblyName(name));
				Type? found = assembly.GetType(fullName, throwOnError: false)
					?? Type.GetType($"{fullName}, {assembly.GetName().Name}", throwOnError: false);
				if (found is not null)
					return found;
				try
				{
					found = assembly.GetExportedTypes().FirstOrDefault(type => type.FullName == fullName);
					if (found is not null)
						return found;
				}
				catch (ReflectionTypeLoadException)
				{
				}
			}
			catch (Exception exception) when (exception is IOException or BadImageFormatException or FileNotFoundException)
			{
			}
		}
		return null;
	}
}
