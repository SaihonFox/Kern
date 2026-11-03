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

	/// <summary>Capabilities, которые требует вызов (spec §7/§16).</summary>
	public IReadOnlyList<string> Capabilities { get; init; } = [];

	/// <summary>
	/// Статический член Kern-типа (<c>[KernStatic]</c>): вызывается через имя типа
	/// (<c>datetime.now()</c>, <c>duration.from_ms(1)</c>), приёмника-экземпляра нет.
	/// </summary>
	public bool IsStaticMember { get; init; }

	/// <summary>Символ оператора (<c>+</c>, <c>&lt;</c>, …) для <c>[KernMember(Operator = …)]</c>.</summary>
	public string? Operator { get; init; }

	public required string ReturnType { get; init; }

	/// <summary>Тип получателя для членов; null для top-level функций и статических членов.</summary>
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

	public override string ToString() => Kern.Vm.Native.NativeKey.Describe(NativeKey);
}

/// <summary>Нативный Kern-тип, объявленный в C#.</summary>
public sealed class NativeTypeDescriptor
{
	public required string Name { get; init; }

	public required Type ClrType { get; init; }

	/// <summary>true — расширение встроенного типа (`str`, `list`, ...), а не новый тип.</summary>
	public bool IsBuiltin { get; init; }

	public int GenericArity { get; init; }

	/// <summary>Имена generic-параметров типа (<c>T</c> у <c>queue&lt;T&gt;</c>, <c>K</c>/<c>V</c> у <c>dict&lt;K,V&gt;</c>).</summary>
	public IReadOnlyList<string> GenericParameterNames { get; init; } = [];

	/// <summary>
	/// Kern-имена прямых родителей из C#-иерархии (у базового класса есть <c>[KernType]</c>):
	/// <c>streamwriter -&gt; stream</c>. Используются для upcast и <c>is</c>.
	/// </summary>
	public IReadOnlyList<string> Parents { get; init; } = [];

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

	/// <summary>
	/// Добавить отражённые .NET-типы (<c>use dotnet.X[...]</c>): возвращает новый каталог
	/// с теми же модулями/типами плюс переданные дескрипторы. Дубликаты ключей отбрасываются.
	/// </summary>
	internal NativeCatalog Extend(
		IEnumerable<NativeTypeDescriptor> extraTypes,
		IEnumerable<NativeFunctionDescriptor> extraFunctions)
	{
		var types = new List<NativeTypeDescriptor>(Types);
		foreach (NativeTypeDescriptor type in extraTypes)
			if (!types.Any(existing => string.Equals(existing.Name, type.Name, StringComparison.Ordinal)))
				types.Add(type);

		var functions = new List<NativeFunctionDescriptor>(Functions);
		var keys = new HashSet<string>(functions.Select(function => function.NativeKey), StringComparer.Ordinal);
		foreach (NativeFunctionDescriptor function in extraFunctions)
			if (keys.Add(function.NativeKey))
				functions.Add(function);

		return new NativeCatalog(Modules, types, functions, Diagnostics);
	}

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
						GenericArity = NativeArity(typeAttribute),
						GenericParameterNames = NativeTypeParameters(typeAttribute),
						Parents = NativeParents(typeAttribute, type),
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
				diagnostics.Add($"повторяющийся нативный член '{Kern.Vm.Native.NativeKey.Describe(function.NativeKey)}'");
				continue;
			}

			allFunctions.Add(function);
		}

		CheckDuplicateModules(modules, diagnostics);
		return new NativeCatalog(modules, types, allFunctions, diagnostics);
	}

	private static IReadOnlyList<string> CapabilitiesOf(Type owner, MethodInfo method)
	{
		var capabilities = new List<string>();
		foreach (KernCapabilityAttribute attribute in owner.GetCustomAttributes<KernCapabilityAttribute>(inherit: false))
			capabilities.Add(attribute.Name);
		foreach (KernCapabilityAttribute attribute in method.GetCustomAttributes<KernCapabilityAttribute>(inherit: false))
			capabilities.Add(attribute.Name);
		return [.. capabilities.Distinct(StringComparer.Ordinal)];
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
			KernCtorAttribute? constructor = method.GetCustomAttribute<KernCtorAttribute>();
			KernStaticAttribute? staticAttribute = method.GetCustomAttribute<KernStaticAttribute>();
			if (member is null && property is null && constructor is null && staticAttribute is null)
				continue;

			// Generic-метод модуля (`json.from_str<T>`): параметры типа метода участвуют в
			// подписи, а reified-значения приходят через `[KernTypeArgs] KType[]`.
			List<string> typeParameters = MethodTypeParameters(method);
			NativeParameterDescriptor[]? parameters = MapParameters(method, typeParameters, skipFirst: 0, diagnostics, out _);
			if (parameters is null)
				continue;
			string returnType = MapReturn(method, typeParameters, diagnostics);
			if (returnType.Length == 0)
				continue;

			NativeMemberKind kind = NativeMemberKind.Method;
			if (constructor is not null)
				kind = NativeMemberKind.Constructor;
			else if (property is not null)
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
				GenericParameters = typeParameters,
				NativeKey = key,
				Capabilities = CapabilitiesOf(type, method),
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
		// Имена generic-параметров: у встроенных контейнеров исторические (T, K/V), иначе —
		// по GenericArity. Self-тип обязан нести аргументы: `queue<T>`, а не `queue`.
		List<string> typeParameters = NativeTypeParameters(attribute);
		string selfType = attribute.Name + FormatTypeArguments(typeParameters);
		string selfKey = attribute.Name + FormatTypeArguments(
			[.. typeParameters.Select(_ => "object")]);

		foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
		{
			if (method.GetCustomAttribute<KernIgnoreAttribute>() is not null)
				continue;
			KernMemberAttribute? member = method.GetCustomAttribute<KernMemberAttribute>();
			KernPropertyAttribute? property = method.GetCustomAttribute<KernPropertyAttribute>();
			KernCtorAttribute? constructor = method.GetCustomAttribute<KernCtorAttribute>();
			KernStaticAttribute? staticAttribute = method.GetCustomAttribute<KernStaticAttribute>();
			if (member is null && property is null && constructor is null && staticAttribute is null)
				continue;

			// Параметры типа метода добавляются к параметрам типа владельца: сигнатура и
			// reified-аргументы (`[KernTypeArgs]`) видны компилятору как одна группа.
			// Параметры типа метода дополняют параметры владельца; одинаковые имена не
			// дублируются (у API-класса `event<T>` член может объявить свой `T`).
			var allParameters = new List<string>(typeParameters);
			foreach (string methodParameter in MethodTypeParameters(method))
				if (!allParameters.Contains(methodParameter))
					allParameters.Add(methodParameter);

			// `[KernCtor]` на статическом методе — фабрика generic-контейнера (`set<T>()`):
			// параметра self нет, вызов targetless, тип результата задаёт подсказка binder'а.
			if (constructor is not null)
			{
				NativeParameterDescriptor[]? factoryParameters = MapParameters(
					method, allParameters, skipFirst: 0, diagnostics, out _);
				if (factoryParameters is null)
					continue;
				string factoryReturn = MapReturn(method, allParameters, diagnostics);
				if (factoryReturn.Length == 0)
					continue;

				string factoryName = KernName(method);
				string factoryKey = NativeKey.Create(
					attribute.Name,
					factoryName,
					NativeMemberKind.Constructor,
					isStatic: true,
					[.. factoryParameters.Select(parameter => parameter.Type)]);
				functions.Add(new NativeFunctionDescriptor
				{
					Name = factoryName,
					Owner = attribute.Name,
					OwnerIsModule = false,
					Kind = NativeMemberKind.Constructor,
					IsStatic = true,
					ReturnType = factoryReturn,
					Parameters = factoryParameters,
					KeyTypes = [.. factoryParameters.Select(parameter => parameter.Type)],
					GenericParameters = allParameters,
					NativeKey = factoryKey,
					Capabilities = CapabilitiesOf(type, method),
					Method = method,
				});
				continue;
			}

			// `[KernStatic]` на методе статического класса — статический член типа
			// (`time.sleep`, `duration.from_ms`): приёмника-экземпляра нет, первый параметр —
			// первый аргумент вызова (spec §16, «нативные статические типы»).
			if (method.GetCustomAttribute<KernStaticAttribute>() is not null)
			{
				NativeParameterDescriptor[]? staticParameters = MapParameters(
					method, allParameters, skipFirst: 0, diagnostics, out _);
				if (staticParameters is null)
					continue;
				string staticReturn = MapReturn(method, allParameters, diagnostics);
				if (staticReturn.Length == 0)
					continue;

				NativeMemberKind staticKind = property is null
					? NativeMemberKind.Method
					: staticParameters.Length == 0 ? NativeMemberKind.PropertyGet : NativeMemberKind.PropertySet;
				string staticName = KernName(method);
				string[] staticKeyTypes = [.. staticParameters.Select(parameter => EraseGenerics(parameter.Type, allParameters))];
				string staticKey = NativeKey.Create(attribute.Name, staticName, staticKind, isStatic: true, staticKeyTypes);
				functions.Add(new NativeFunctionDescriptor
				{
					Name = staticName,
					Owner = attribute.Name,
					OwnerIsModule = false,
					Kind = staticKind,
					IsStatic = true,
					IsStaticMember = true,
					ReturnType = staticReturn,
					Parameters = staticParameters,
					KeyTypes = staticKeyTypes,
					GenericParameters = allParameters,
					NativeKey = staticKey,
					Capabilities = CapabilitiesOf(type, method),
					Method = method,
				});
				continue;
			}

			NativeParameterDescriptor[]? parameters = MapParameters(
				method,
				allParameters,
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

			string returnType = MapReturn(method, allParameters, diagnostics);
			if (returnType.Length == 0)
				continue;

			NativeMemberKind kind = NativeMemberKind.Method;
			if (property is not null)
				kind = parameters.Length == 0 ? NativeMemberKind.PropertyGet : NativeMemberKind.PropertySet;

			string name = KernName(method);
			string[] keyTypes = [selfKey, .. parameters.Select(parameter => EraseGenerics(parameter.Type, allParameters))];
			string key = NativeKey.Create(attribute.Name, name, kind, isStatic: true, keyTypes);
			functions.Add(new NativeFunctionDescriptor
			{
				Name = name,
				Owner = attribute.Name,
				OwnerIsModule = false,
				Kind = kind,
				IsStatic = true,
				Operator = member?.Operator,
				ReturnType = returnType,
				SelfType = selfType,
				Parameters = parameters,
				KeyTypes = keyTypes,
				GenericParameters = allParameters,
				NativeKey = key,
				Capabilities = CapabilitiesOf(type, method),
				Method = method,
			});
		}

		return functions;
	}

	/// <summary>Имена generic-параметров метода; пусто у негенерического метода.</summary>
	private static List<string> MethodTypeParameters(MethodInfo method) =>
		method.IsGenericMethodDefinition
			? [.. method.GetGenericArguments().Select(argument => argument.Name)]
			: [];

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
			// `[KernTypeArgs] KType[]` — служебный параметр: компилятор передаёт сюда
			// reified-аргументы типа, в Kern-подписи его нет.
			if (parameter.GetCustomAttribute<KernTypeArgsAttribute>() is not null)
				continue;
			if (skipped++ < skipFirst)
				continue;

			// Явный Kern-тип параметра (`pipe<T>`) важнее CLR-сигнатуры моста.
			if (parameter.GetCustomAttribute<KernParameterTypeAttribute>() is { } declaredType)
			{
				string declaredName = parameter.GetCustomAttribute<KernNameAttribute>()?.Name
					?? NameMapping.ToSnakeCase(parameter.Name ?? $"arg{parameters.Count}");
				parameters.Add(new NativeParameterDescriptor(declaredName, declaredType.Type));
				continue;
			}

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
		if (method.GetCustomAttribute<KernReturnAttribute>() is { } declared)
			return declared.Type;

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
		"list" or "array" or "set" or "queue" or "stack" or "observable" or "collection" => 1,
		"dict" => 2,
		_ => 0,
	};

	/// <summary>
	/// Арность нативного типа: явная <see cref="KernTypeAttribute.GenericArity"/> важнее
	/// таблицы встроенных имён, иначе — историческая таблица (<c>list</c>/<c>dict</c>).
	/// </summary>
	private static int NativeArity(KernTypeAttribute attribute) =>
		Math.Max(attribute.GenericArity, BuiltinArity(attribute));

	/// <summary>
	/// Прямые Kern-родители из C#-иерархии: <c>StreamWriterHandle : StreamHandle</c> даёт
	/// <c>streamwriter -&gt; stream</c>. Учитываются только базовые классы с <c>[KernType]</c>;
	/// generic-родители пока не разворачиваются (нужны аргументы типа).
	/// </summary>
	private static List<string> NativeParents(KernTypeAttribute attribute, Type type)
	{
		var parents = new List<string>();
		if (attribute.Parent is { Length: > 0 } declared)
			parents.Add(declared);
		for (Type? baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
		{
			KernTypeAttribute? inherited = TypeMapping.NativeAttribute(baseType);
			if (inherited is not null)
				parents.Add(inherited.Name);
		}

		return parents;
	}

	private static List<string> BuiltinTypeParameters(KernTypeAttribute attribute) => attribute.Name switch
	{
		"list" or "array" or "set" or "queue" or "stack" or "observable" or "collection" => ["T"],
		"dict" => ["K", "V"],
		_ => [],
	};

	/// <summary>
	/// Имена generic-параметров нативного типа: у встроенных контейнеров они исторические
	/// (<c>T</c>, <c>K</c>/<c>V</c>), у новых native-типов — <c>T</c> или <c>T1..Tn</c>.
	/// </summary>
	private static List<string> NativeTypeParameters(KernTypeAttribute attribute)
	{
		List<string> builtin = BuiltinTypeParameters(attribute);
		if (builtin.Count > 0)
			return builtin;

		var names = new List<string>(attribute.GenericArity);
		for (var index = 0; index < attribute.GenericArity; index++)
			names.Add(attribute.GenericArity == 1 ? "T" : $"T{index + 1}");
		return names;
	}

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
