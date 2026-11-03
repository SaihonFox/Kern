using System.Reflection;
using Kern.Binding.Mapping;
using Kern.Vm.Native;

namespace Kern.Binding.Catalog;

/// <summary>
/// Импорт .NET-типов через <c>use dotnet.Namespace[T1, T2]</c> (spec §12.10, «способ C»).
/// Статический класс даёт статические методы/свойства; обычный класс — конструкторы и
/// экземплярные методы/свойства (приёмник передаётся первым аргументом). Имена — snake_case.
/// </summary>
public static class DotnetCatalog
{
	public static NativeCatalog Import(NativeCatalog catalog, IEnumerable<Type> types)
	{
		ArgumentNullException.ThrowIfNull(catalog);
		ArgumentNullException.ThrowIfNull(types);

		var extraTypes = new List<NativeTypeDescriptor>();
		var extraFunctions = new List<NativeFunctionDescriptor>();

		foreach (Type type in types.Distinct())
		{
			if (type.IsGenericTypeDefinition || type.Name.Contains('`', StringComparison.Ordinal))
				continue;

			string typeName = type.Name;
			var members = new List<NativeFunctionDescriptor>();
			if (type.IsAbstract && type.IsSealed)
				ImportStatic(type, typeName, members);
			else
				ImportInstance(type, typeName, members);

			if (members.Count == 0)
				continue;
			extraTypes.Add(new NativeTypeDescriptor
			{
				Name = typeName,
				ClrType = type,
				Members = members,
			});
			extraFunctions.AddRange(members);
		}

		return catalog.Extend(extraTypes, extraFunctions);
	}

	private static void ImportStatic(Type type, string typeName, List<NativeFunctionDescriptor> members)
	{
		foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
		{
			if (method.IsSpecialName || method.IsGenericMethodDefinition || method.DeclaringType == typeof(object))
				continue;
			if (TryBuild(method, type, typeName, NativeMemberKind.Method, out NativeFunctionDescriptor? descriptor))
				members.Add(descriptor!);
		}

		foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Static))
		{
			MethodInfo? getter = property.GetMethod;
			if (getter is null || !getter.IsPublic || getter.IsGenericMethodDefinition)
				continue;
			if (TryBuild(getter, type, typeName, NativeMemberKind.PropertyGet, out NativeFunctionDescriptor? descriptor))
				members.Add(descriptor!);
		}
	}

	private static void ImportInstance(Type type, string typeName, List<NativeFunctionDescriptor> members)
	{
		foreach (ConstructorInfo constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
		{
			if (TryBuildConstructor(constructor, type, typeName, out NativeFunctionDescriptor? descriptor))
				members.Add(descriptor!);
		}

		foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
		{
			if (method.IsSpecialName || method.IsGenericMethodDefinition || method.DeclaringType == typeof(object))
				continue;
			if (TryBuildInstance(method, type, typeName, NativeMemberKind.Method, out NativeFunctionDescriptor? descriptor))
				members.Add(descriptor!);
		}

		foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			MethodInfo? getter = property.GetMethod;
			if (getter is not null && getter.IsPublic && !getter.IsGenericMethodDefinition &&
				TryBuildInstance(getter, type, typeName, NativeMemberKind.PropertyGet, out NativeFunctionDescriptor? read))
				members.Add(read!);
			MethodInfo? setter = property.SetMethod;
			if (setter is not null && setter.IsPublic && !setter.IsGenericMethodDefinition &&
				TryBuildInstance(setter, type, typeName, NativeMemberKind.PropertySet, out NativeFunctionDescriptor? write))
				members.Add(write!);
		}
	}

	/// <summary>Статический член: без приёмника, ключ <c>dotnet:Type|name|kind|t|params</c>.</summary>
	internal static bool TryBuild(
		MethodInfo method,
		Type owner,
		string ownerName,
		NativeMemberKind kind,
		out NativeFunctionDescriptor? descriptor)
	{
		descriptor = null;
		if (!TryMap(method.ReturnType, ownerName, out string returnType))
			return false;

		ParameterInfo[] parameters = method.GetParameters();
		var mapped = new List<NativeParameterDescriptor>(parameters.Length);
		var keyTypes = new List<string>(parameters.Length);
		foreach (ParameterInfo parameter in parameters)
		{
			if (!TryMap(parameter.ParameterType, ownerName, out string typeName))
				return false;
			mapped.Add(new NativeParameterDescriptor(
				NameMapping.ToSnakeCase(parameter.Name ?? $"arg{mapped.Count}"), typeName));
			keyTypes.Add(typeName);
		}

		string name = NameMapping.ToSnakeCase(kind == NativeMemberKind.PropertyGet
			? method.Name.StartsWith("get_", StringComparison.Ordinal) ? method.Name[4..] : method.Name
			: method.Name);
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
		NativeMemberKind kind,
		out NativeFunctionDescriptor? descriptor)
	{
		descriptor = null;
		if (!TryMap(method.ReturnType, ownerName, out string returnType))
			return false;

		ParameterInfo[] parameters = method.GetParameters();
		var mapped = new List<NativeParameterDescriptor>(parameters.Length);
		var keyTypes = new List<string>(parameters.Length + 1) { ownerName };
		foreach (ParameterInfo parameter in parameters)
		{
			if (!TryMap(parameter.ParameterType, ownerName, out string typeName))
				return false;
			mapped.Add(new NativeParameterDescriptor(
				NameMapping.ToSnakeCase(parameter.Name ?? $"arg{mapped.Count}"), typeName));
			keyTypes.Add(typeName);
		}

		string name = NameMapping.ToSnakeCase(kind == NativeMemberKind.PropertyGet
			? method.Name.StartsWith("get_", StringComparison.Ordinal) ? method.Name[4..] : method.Name
			: method.Name);
		string key = NativeKey.Create("dotnet:" + owner.FullName, name, kind, isStatic: true, keyTypes);
		descriptor = new NativeFunctionDescriptor
		{
			Name = name,
			Owner = ownerName,
			OwnerIsModule = false,
			Kind = kind,
			IsStatic = false,
			ReturnType = returnType,
			SelfType = ownerName,
			SelfClrType = owner,
			Parameters = mapped,
			KeyTypes = keyTypes,
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
		out NativeFunctionDescriptor? descriptor)
	{
		descriptor = null;
		ParameterInfo[] parameters = constructor.GetParameters();
		var mapped = new List<NativeParameterDescriptor>(parameters.Length);
		var keyTypes = new List<string>(parameters.Length);
		foreach (ParameterInfo parameter in parameters)
		{
			if (!TryMap(parameter.ParameterType, ownerName, out string typeName))
				return false;
			mapped.Add(new NativeParameterDescriptor(
				NameMapping.ToSnakeCase(parameter.Name ?? $"arg{mapped.Count}"), typeName));
			keyTypes.Add(typeName);
		}

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
			ReturnType = ownerName,
			Parameters = mapped,
			KeyTypes = keyTypes,
			NativeKey = key,
			Constructor = constructor,
		};
		return true;
	}

	/// <summary>
	/// CLR-тип → Kern-имя для <c>use dotnet</c>: известные типы через <see cref="TypeMapping"/>,
	/// остальные негенерические классы/структуры — по простому имени (тип импортируется тем же
	/// <c>use dotnet</c>). Открытые generic-типы и массивы неизвестных элементов отклоняются.
	/// </summary>
	private static bool TryMap(Type type, string? ownerName, out string name)
	{
		if (TypeMapping.TryToKern(type, null, out name))
			return true;

		Type nullable = Nullable.GetUnderlyingType(type) ?? type;
		if (nullable != type)
		{
			if (!TryMap(nullable, ownerName, out string underlying))
				return false;
			name = underlying + "?";
			return true;
		}

		if (type.IsArray)
		{
			if (!TryMap(type.GetElementType()!, ownerName, out string element))
				return false;
			name = $"array<{element}>";
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

	/// <summary>Найти .NET-тип по полному имени (в загруженных сборках и известных BCL).</summary>
	public static Type? FindType(string fullName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
		Type? direct = Type.GetType(fullName, throwOnError: false);
		if (direct is not null)
			return direct;
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
