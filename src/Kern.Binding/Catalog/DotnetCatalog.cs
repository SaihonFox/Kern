using System.Reflection;
using Kern.Binding.Mapping;
using Kern.Vm.Native;

namespace Kern.Binding.Catalog;

/// <summary>
/// Импорт обычных .NET-типов через <c>use dotnet.Namespace[T1, T2]</c> (spec §12.10,
/// «способ C»). Статические методы и свойства отражаются в Kern как статические члены
/// типа с snake_case-именами: <c>File.read_all_text(path)</c>, <c>Path.combine(a, b)</c>.
/// Экземплярные члены и открытые generic-типы пока не импортируются.
/// </summary>
public static class DotnetCatalog
{
	/// <summary>
	/// Расширить каталог типами. Неподдержанные перегрузки просто пропускаются; если тип
	/// не найден, вызывающий добавляет диагностику сам.
	/// </summary>
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
			if (!type.IsAbstract || !type.IsSealed)
			{
				// Пока поддержаны статические классы; для экземплярных нужен ctor/receiver.
				if (!type.GetMethods(BindingFlags.Public | BindingFlags.Static)
					.Any(method => !method.IsSpecialName && !method.IsGenericMethodDefinition))
					continue;
			}

			string typeName = type.Name;
			var members = new List<NativeFunctionDescriptor>();
			foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
			{
				if (method.IsSpecialName || method.IsGenericMethodDefinition)
					continue;
				if (method.DeclaringType == typeof(object))
					continue;
				if (TryBuild(method, type, typeName, NativeMemberKind.Method, out var descriptor))
					members.Add(descriptor!);
			}

			foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Static))
			{
				MethodInfo? getter = property.GetMethod;
				if (getter is null || !getter.IsPublic || getter.IsGenericMethodDefinition)
					continue;
				if (TryBuild(getter, type, typeName, NativeMemberKind.PropertyGet, out var descriptor))
					members.Add(descriptor!);
			}

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

	/// <summary>Найти .NET-тип по полному имени среди загруженных сборок (и подгружая System.*).</summary>
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

		// Часто встречающиеся сборки BCL; после загрузки повторяем поиск.
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

	internal static bool TryBuild(
		MethodInfo method,
		Type owner,
		string ownerName,
		NativeMemberKind kind,
		out NativeFunctionDescriptor? descriptor)
	{
		descriptor = null;
		if (!TypeMapping.TryToKern(method.ReturnType, null, out string returnType))
			return false;

		ParameterInfo[] parameters = method.GetParameters();
		var mapped = new List<NativeParameterDescriptor>(parameters.Length);
		var keyTypes = new List<string>(parameters.Length);
		foreach (ParameterInfo parameter in parameters)
		{
			if (!TypeMapping.TryToKern(parameter.ParameterType, null, out string typeName))
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
}
