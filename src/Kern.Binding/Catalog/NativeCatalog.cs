using System.Reflection;
using System.Text.RegularExpressions;
using Kern.Binding.Attributes;
using Kern.Binding.Mapping;
using Kern.Vm.Native;

namespace Kern.Binding.Catalog;

/// <summary>Один параметр нативной функции в Kern-сигнатуре.</summary>
public sealed record NativeParameterDescriptor(string Name, string Type);

/// <summary>
/// Описание нативной функции/свойства/конструктора для компилятора и VM.
/// <see cref="NativeKey"/> — ключ диспетчеризации; <see cref="Method"/> — CLR-метод,
/// из которого <c>NativeResolver</c> строит скомпилированный делегат.
/// </summary>
public sealed class NativeFunctionDescriptor
{
	public required string Name { get; init; }

	/// <summary>Модуль (`kern.core`) или Kern-тип (`str`/`list`).</summary>
	public required string Owner { get; init; }

	public bool OwnerIsModule { get; init; }

	public NativeMemberKind Kind { get; init; } = NativeMemberKind.Method;

	public bool IsStatic { get; init; } = true;

	public required string ReturnType { get; init; }

	/// <summary>Тип получателя для членов; null для top-level функций.</summary>
	public string? SelfType { get; init; }

	/// <summary>Параметры Kern-сигнатуры без `self`.</summary>
	public IReadOnlyList<NativeParameterDescriptor> Parameters { get; init; } = [];

	/// <summary>Типы в <see cref="NativeKey"/>: `self` (если есть) и параметры, generic-параметры стёрты в object.</summary>
	public IReadOnlyList<string> KeyTypes { get; init; } = [];

	/// <summary>Синтетические generic-параметры (для builtin-контейнеров: T, K, V).</summary>
	public IReadOnlyList<string> GenericParameters { get; init; } = [];

	public required string NativeKey { get; init; }

	public MethodInfo? Method { get; init; }

	public bool IsBound => Method is not null;

	/// <summary>Число аргументов, которое VM передаёт в делегат (self + параметры).</summary>
	public int SignatureParameterCount => (SelfType is null ? 0 : 1) + Parameters.Count;

	public override string ToString() => NativeKey;
}

/// <summary>Нативный Kern-тип, объявленный в C#.</summary>
public sealed class NativeTypeDescriptor
{
	public required string Name { get; init; }

	public required Type ClrType { get; init; }

	/// <summary>true — расширение встроенного типа (`str`, `list`, ...), а не новый тип.</summary>
	public bool IsBuiltin { get; init; }

	public int GenericArity { get; init; }

	public IReadOnlyList<NativeFunctionDescriptor> Members { get; init; } = [];

	public override string ToString() => $"native type {Name}";
}

/// <summary>Нативный модуль (`[KernModule("kern.core")]`).</summary>
public sealed class NativeModuleDescriptor
{
	public required string Name { get; init; }

	public required Type ClrType { get; init; }

	public IReadOnlyList<NativeFunctionDescriptor> Functions { get; init; } = [];

	public override string ToString() => $"native module {Name}";
}

/// <summary>
/// Результат сканирования сборок: native-модули, типы и плоский список функций
/// (spec §12.9). Компилятор превращает его в символы, VM — в делегаты.
/// </summary>
public sealed class NativeCatalog
{
	private static readonly Regex ParameterWord = new(
		@"(?<![A-Za-z0-9_])(?<name>[A-Za-z_][A-Za-z0-9_]*)(?![A-Za-z0-9_])",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private NativeCatalog(
		IReadOnlyList<NativeModuleDescriptor> modules,
		IReadOnlyList<NativeTypeDescriptor> types,
		IReadOnlyList<NativeFunctionDescriptor> functions,
		IReadOnlyList<string> diagnostics)
	{
		Modules = modules;
		Types = types;
		Functions = functions;
		Diagnostics = diagnostics;
	}

	public IReadOnlyList<NativeModuleDescriptor> Modules { get; }

	public IReadOnlyList<NativeTypeDescriptor> Types { get; }

	public IReadOnlyList<NativeFunctionDescriptor> Functions { get; }

	public IReadOnlyList<string> Diagnostics { get; }

	public bool IsEmpty => Functions.Count == 0 && Types.Count == 0 && Modules.Count == 0;

	/// <summary>Сканировать сборки; повторяющиеся ключи считаются ошибкой каталога.</summary>
	public static NativeCatalog Scan(params Assembly[] assemblies) =>
		Scan((IEnumerable<Assembly>)assemblies);

	public static NativeCatalog Scan(IEnumerable<Assembly> assemblies)
	{
		ArgumentNullException.ThrowIfNull(assemblies);
		var modules = new List<NativeModuleDescriptor>();
		var types = new List<NativeTypeDescriptor>();
		var diagnostics = new List<string>();

		foreach (Assembly assembly in assemblies.Distinct())
		{
			foreach (Type type in SafeTypes(assembly, diagnostics))
			{
				KernModuleAttribute? moduleAttribute = type
					.GetCustomAttributes(typeof(KernModuleAttribute), inherit: false)
					.OfType<KernModuleAttribute>()
					.FirstOrDefault();
				if (moduleAttribute is not null)
				{
					IReadOnlyList<NativeFunctionDescriptor> functions = BuildModule(moduleAttribute.Name, type, diagnostics);
					if (functions.Count > 0)
						modules.Add(new NativeModuleDescriptor { Name = moduleAttribute.Name, ClrType = type, Functions = functions });
					continue;
				}

				KernTypeAttribute? typeAttribute = type
					.GetCustomAttributes(typeof(KernTypeAttribute), inherit: false)
					.OfType<KernTypeAttribute>()
					.FirstOrDefault();
				if (typeAttribute is not null)
				{
					if (type.IsGenericTypeDefinition)
					{
						diagnostics.Add($"тип '{type.FullName}' — открытый generic; укажите конкретные члены в отдельном классе");
						continue;
					}

					IReadOnlyList<NativeFunctionDescriptor> members = BuildNativeType(typeAttribute, type, diagnostics);
					types.Add(new NativeTypeDescriptor
					{
						Name = typeAttribute.Name,
						ClrType = type,
						IsBuiltin = typeAttribute.IsBuiltin,
						GenericArity = BuiltinArity(typeAttribute),
						Members = members,
					});
				}
			}
		}

		var allFunctions = new List<NativeFunctionDescriptor>();
		var keys = new HashSet<string>(StringComparer.Ordinal);
		foreach (NativeFunctionDescriptor function in
			modules.SelectMany(module => module.Functions)
				.Concat(types.SelectMany(type => type.Members)))
		{
			if (!keys.Add(function.NativeKey))
			{
				diagnostics.Add($"повторяющийся NativeKey '{function.NativeKey}'");
				continue;
			}

			allFunctions.Add(function);
		}

		CheckDuplicateModules(modules, diagnostics);
		return new NativeCatalog(modules, types, allFunctions, diagnostics);
	}

	private static void CheckDuplicateModules(IReadOnlyList<NativeModuleDescriptor> modules, List<string> diagnostics)
	{
		foreach (var group in modules.GroupBy(module => module.Name, StringComparer.Ordinal))
		{
			if (group.Count() > 1)
				diagnostics.Add($"модуль '{group.Key}' объявлен несколькими C#-классами");
		}
	}

	private static IReadOnlyList<NativeFunctionDescriptor> BuildModule(
		string moduleName,
		Type type,
		List<string> diagnostics)
	{
		var functions = new List<NativeFunctionDescriptor>();
		foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
		{
			if (method.GetCustomAttribute<KernIgnoreAttribute>() is not null)
				continue;
			KernMemberAttribute? member = method.GetCustomAttribute<KernMemberAttribute>();
			KernPropertyAttribute? property = method.GetCustomAttribute<KernPropertyAttribute>();
			if (member is null && property is null)
				continue;
			if (method.IsGenericMethodDefinition)
			{
				diagnostics.Add($"модульная функция '{type.Name}.{method.Name}' generic; поддержка появится позже");
				continue;
			}

			NativeParameterDescriptor[]? parameters = MapParameters(method, typeParameters: null, skipFirst: 0, diagnostics, out _);
			if (parameters is null)
				continue;
			string returnType = MapReturn(method, typeParameters: null, diagnostics);
			if (returnType.Length == 0)
				continue;

			NativeMemberKind kind = NativeMemberKind.Method;
			if (property is not null)
			{
				kind = parameters.Length == 0 ? NativeMemberKind.PropertyGet : NativeMemberKind.PropertySet;
			}

			string name = KernName(method);
			string key = NativeKey.Create(moduleName, name, kind, isStatic: true, [.. parameters.Select(parameter => parameter.Type)]);
			functions.Add(new NativeFunctionDescriptor
			{
				Name = name,
				Owner = moduleName,
				OwnerIsModule = true,
				Kind = kind,
				IsStatic = true,
				ReturnType = returnType,
				Parameters = parameters,
				KeyTypes = [.. parameters.Select(parameter => parameter.Type)],
				NativeKey = key,
				Method = method,
			});
		}

		return functions;
	}

	private static IReadOnlyList<NativeFunctionDescriptor> BuildNativeType(
		KernTypeAttribute attribute,
		Type type,
		List<string> diagnostics)
	{
		var functions = new List<NativeFunctionDescriptor>();
		List<string> typeParameters = BuiltinTypeParameters(attribute);
		string selfType = attribute.Name + FormatTypeArguments(typeParameters);
		string selfKey = attribute.Name + FormatTypeArguments(
			[.. typeParameters.Select(_ => "object")]);

		foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
		{
			if (method.GetCustomAttribute<KernIgnoreAttribute>() is not null)
				continue;
			KernMemberAttribute? member = method.GetCustomAttribute<KernMemberAttribute>();
			KernPropertyAttribute? property = method.GetCustomAttribute<KernPropertyAttribute>();
			if (member is null && property is null)
				continue;
			if (method.IsGenericMethodDefinition)
			{
				diagnostics.Add($"метод '{type.Name}.{method.Name}' generic; для встроенных типов используйте KValue-сигнатуры");
				continue;
			}

			NativeParameterDescriptor[]? parameters = MapParameters(
				method,
				typeParameters,
				skipFirst: 1,
				diagnostics,
				out bool hasSelf);
			if (parameters is null)
				continue;
			if (!hasSelf)
			{
				diagnostics.Add($"у члена '{type.Name}.{method.Name}' нет параметра self");
				continue;
			}

			string returnType = MapReturn(method, typeParameters, diagnostics);
			if (returnType.Length == 0)
				continue;

			NativeMemberKind kind = NativeMemberKind.Method;
			if (property is not null)
				kind = parameters.Length == 0 ? NativeMemberKind.PropertyGet : NativeMemberKind.PropertySet;

			string name = KernName(method);
			string[] keyTypes = [selfKey, .. parameters.Select(parameter => EraseGenerics(parameter.Type, typeParameters))];
			string key = NativeKey.Create(attribute.Name, name, kind, isStatic: true, keyTypes);
			functions.Add(new NativeFunctionDescriptor
			{
				Name = name,
				Owner = attribute.Name,
				OwnerIsModule = false,
				Kind = kind,
				IsStatic = true,
				ReturnType = returnType,
				SelfType = selfType,
				Parameters = parameters,
				KeyTypes = keyTypes,
				GenericParameters = typeParameters,
				NativeKey = key,
				Method = method,
			});
		}

		return functions;
	}

	private static NativeParameterDescriptor[]? MapParameters(
		MethodInfo method,
		IReadOnlyList<string>? typeParameters,
		int skipFirst,
		List<string> diagnostics,
		out bool hasSelf)
	{
		ParameterInfo[] all = method.GetParameters();
		var parameters = new List<NativeParameterDescriptor>(Math.Max(0, all.Length - skipFirst));
		hasSelf = skipFirst == 0 || all.Length > 0;
		var skipped = 0;
		foreach (ParameterInfo parameter in all)
		{
			if (parameter.ParameterType == typeof(Kern.Vm.Execution.Interpreter))
				continue;
			if (skipped++ < skipFirst)
				continue;

			if (!TypeMapping.TryToKern(parameter.ParameterType, typeParameters, out string typeName))
			{
				diagnostics.Add(
					$"параметр '{method.DeclaringType?.Name}.{method.Name}({parameter.Name})' типа {parameter.ParameterType} не поддержан");
				return null;
			}

			string name = parameter.GetCustomAttribute<KernNameAttribute>()?.Name
				?? NameMapping.ToSnakeCase(parameter.Name ?? $"arg{parameters.Count}");
			parameters.Add(new NativeParameterDescriptor(name, typeName));
		}

		return [.. parameters];
	}

	private static string MapReturn(MethodInfo method, IReadOnlyList<string>? typeParameters, List<string> diagnostics)
	{
		if (!TypeMapping.TryToKern(method.ReturnType, typeParameters, out string returnType))
		{
			diagnostics.Add(
				$"возвращаемый тип '{method.DeclaringType?.Name}.{method.Name}' ({method.ReturnType}) не поддержан");
			return "";
		}

		// Аннотация `string?`/`T?` у ссылочного типа стирается в CLR: `?` берётся из метаданных
		// nullable, иначе `find(...) -> str?` стал бы `str` и сравнивать его с nil было бы нельзя
		// (spec §12.4).
		if (!method.ReturnType.IsValueType &&
			IsNullableReference(method.ReturnParameter) &&
			!returnType.EndsWith('?'))
		{
			returnType += "?";
		}

		return returnType;
	}

	/// <summary>Возвращаемый параметр помечен как nullable (`string?`, `T?`) в метаданных сборки.</summary>
	private static bool IsNullableReference(ParameterInfo parameter) =>
		new NullabilityInfoContext().Create(parameter).ReadState == NullabilityState.Nullable;

	private static string KernName(MethodInfo method) =>
		method.GetCustomAttribute<KernNameAttribute>()?.Name ?? NameMapping.ToSnakeCase(method.Name);

	private static int BuiltinArity(KernTypeAttribute attribute) => attribute.Name switch
	{
		"list" or "array" or "set" => 1,
		"dict" => 2,
		_ => 0,
	};

	private static List<string> BuiltinTypeParameters(KernTypeAttribute attribute) => attribute.Name switch
	{
		"list" or "array" or "set" => ["T"],
		"dict" => ["K", "V"],
		_ => [],
	};

	private static string FormatTypeArguments(IReadOnlyList<string> arguments) =>
		arguments.Count == 0 ? "" : $"<{string.Join(",", arguments)}>";

	internal static string EraseGenerics(string typeName, IReadOnlyList<string> typeParameters)
	{
		if (typeParameters.Count == 0 || !typeName.Contains('<') && !typeParameters.Contains(typeName))
			return typeName;

		return ParameterWord.Replace(
			typeName,
			match => typeParameters.Contains(match.Groups["name"].Value) ? "object" : match.Value);
	}

	private static IEnumerable<Type> SafeTypes(Assembly assembly, List<string> diagnostics)
	{
		try
		{
			return assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException exception)
		{
			foreach (Exception? loader in exception.LoaderExceptions)
				if (loader is not null)
					diagnostics.Add($"не удалось загрузить тип из '{assembly.GetName().Name}': {loader.Message}");
			return exception.Types.Where(type => type is not null)!;
		}
	}
}
