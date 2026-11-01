using Kern.Binding.Catalog;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Native;

/// <summary>
/// Превращает <see cref="NativeCatalog"/> в символы компилятора (spec §12.9):
/// нативные типы — в <see cref="ModuleSymbol.Types"/>, top-level функции — в
/// <see cref="ModuleSymbol.Functions"/>, члены встроенных типов — в
/// <see cref="ModuleSymbol.NativeFunctions"/>.
/// </summary>
public static class NativeSymbolBuilder
{
	/// <summary>Подключить каталог к модулю. Возвращает новый ModuleSymbol и диагностику.</summary>
	public static ModuleSymbol Import(
		ModuleSymbol module,
		NativeCatalog catalog,
		out IReadOnlyList<string> diagnostics)
	{
		ArgumentNullException.ThrowIfNull(module);
		ArgumentNullException.ThrowIfNull(catalog);

		var problems = new List<string>();

		// Нативные типы регистрируются до разбора сигнатур: иначе `[KernType("streamwriter")]`
		// не может сослаться сам на себя ни в параметрах, ни в возвращаемом типе.
		var types = new Dictionary<string, TypeSymbol>(module.Types, StringComparer.Ordinal);
		RegisterTypes(types, catalog, problems);
		types.TryAdd(ResultTypes.OkName, ResultTypes.Ok);
		types.TryAdd(ResultTypes.ErrName, ResultTypes.Err);
		types.TryAdd(ResultTypes.OkBaseName, ResultTypes.OkMarker);
		types.TryAdd(ResultTypes.ErrBaseName, ResultTypes.ErrMarker);

		var natives = new List<FunctionSymbol>();
		foreach (NativeFunctionDescriptor descriptor in catalog.Functions)
		{
			if (descriptor.Method is not null && !descriptor.Method.IsStatic)
			{
				problems.Add($"native '{descriptor.NativeKey}': экземплярные члены пока не поддержаны");
				continue;
			}

			if (!TryBuild(types, descriptor, out FunctionSymbol? symbol, out string? error))
			{
				problems.Add($"native '{descriptor.NativeKey}': {error}");
				continue;
			}

			natives.Add(symbol);
		}

		AttachStaticMembers(types, natives, problems);

		var functions = new Dictionary<string, List<FunctionSymbol>>(StringComparer.Ordinal);
		foreach (var (name, group) in module.Functions)
			functions[name] = [.. group];

		foreach (FunctionSymbol native in natives)
		{
			if (native.NativeSelfType is not null || native.NativeOwnerType is not null)
				continue;
			if (!functions.TryGetValue(native.Name, out List<FunctionSymbol>? group))
				functions[native.Name] = group = [];
			if (!group.Any(existing => existing.Signature == native.Signature && !existing.IsNative))
				group.Add(native);
		}

		var merged = functions.ToDictionary(
			pair => pair.Key,
			pair => (IReadOnlyList<FunctionSymbol>)pair.Value,
			StringComparer.Ordinal);

		diagnostics = problems;
		return module with
		{
			Types = types,
			Functions = merged,
			NativeFunctions = natives
		};
	}

	/// <summary>
	/// Нативные типы каталога как символы — нужны до сбора деклараций: поле класса может
	/// иметь нативный тип (<c>mutex gate</c>, <c>event&lt;str&gt; clicked</c>), а сборщик
	/// деклараций работает раньше <see cref="Import"/>.
	/// </summary>
	public static IReadOnlyDictionary<string, TypeSymbol> CreateTypeSymbols(NativeCatalog catalog)
	{
		ArgumentNullException.ThrowIfNull(catalog);
		var types = new Dictionary<string, TypeSymbol>(StringComparer.Ordinal);
		foreach (NativeTypeDescriptor descriptor in catalog.Types)
		{
			if (IsBuiltinName(descriptor.Name) || types.ContainsKey(descriptor.Name))
				continue;

			if (descriptor.GenericArity > 0)
			{
				var names = descriptor.GenericParameterNames.Count == descriptor.GenericArity
					? descriptor.GenericParameterNames
					: DefaultGenericNames(descriptor.GenericArity);
				types[descriptor.Name] = new TypeSymbol
				{
					Name = descriptor.Name,
					Kind = SymbolKind.Native,
					GenericParameterNames = [.. names],
					GenericParameters = [.. names.Select(name => new TypeParameterType(name, []))],
				};
				continue;
			}

			types[descriptor.Name] = new TypeSymbol { Name = descriptor.Name, Kind = SymbolKind.Native };
		}

		return types;
	}

	/// <summary>
	/// Регистрирует нативные Kern-типы каталога в таблице типов модуля. Имя примитива или
	/// встроенного generic-типа (<c>str</c>, <c>list</c>, <c>object</c>) не регистрируется:
	/// такой <c>[KernType]</c>-класс расширяет существующий тип, а не объявляет новый.
	/// Одна и та же нативная запись может встречаться дважды (bridge-класс и handle), поэтому
	/// повтор имени нативного типа молча пропускается — выигрывает первый символ.
	/// </summary>
	private static void RegisterTypes(
		Dictionary<string, TypeSymbol> types,
		NativeCatalog catalog,
		List<string> problems)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (NativeTypeDescriptor descriptor in catalog.Types)
		{
			if (IsBuiltinName(descriptor.Name))
				continue;

			// Второй описатель того же Kern-имени (bridge-класс и класс handle) — не повод для
			// диагностики: у модуля тот же тип, один символ, члены всё равно лежат в NativeFunctions.
			if (!seen.Add(descriptor.Name))
				continue;

			if (types.TryGetValue(descriptor.Name, out TypeSymbol? existing))
			{
				if (existing.Kind != SymbolKind.Native)
					problems.Add($"нативный тип '{descriptor.Name}' конфликтует с типом, объявленным в модуле");
				continue;
			}

			if (descriptor.GenericArity > 0)
			{
				// Generic-нативные контейнеры (`set<T>`, `queue<T>`): символ типа с именами
				// параметров; члены регистрируются в NativeFunctions с SelfType содержащим T,
				// а подстановку аргументов делает binder (spec §16).
				var names = descriptor.GenericParameterNames.Count == descriptor.GenericArity
					? descriptor.GenericParameterNames
					: DefaultGenericNames(descriptor.GenericArity);
				types[descriptor.Name] = new TypeSymbol
				{
					Name = descriptor.Name,
					Kind = SymbolKind.Native,
					GenericParameterNames = [.. names],
					GenericParameters = [.. names.Select(name => new TypeParameterType(name, []))],
				};
				continue;
			}

			types[descriptor.Name] = new TypeSymbol
			{
				Name = descriptor.Name,
				Kind = SymbolKind.Native
			};
		}

		// Второй проход: родители из C#-иерархии (`streamwriter -> stream`) и явные
		// `[KernType(Parent = "async_iter<T>")]` — после регистрации всех символов.
		foreach (NativeTypeDescriptor descriptor in catalog.Types)
		{
			if (descriptor.Parents.Count == 0 ||
				!types.TryGetValue(descriptor.Name, out TypeSymbol? child) ||
				child.Kind != SymbolKind.Native)
				continue;

			var variables = new Dictionary<string, TypeParameterType>(StringComparer.Ordinal);
			foreach (TypeParameterType parameter in child.GenericParameters)
				variables[parameter.Name] = parameter;

			List<KernType> bases = [];
			foreach (string parent in descriptor.Parents)
			{
				try
				{
					KernType resolved = NativeTypeParser.Parse(
						parent,
						variables,
						name => types.TryGetValue(name, out TypeSymbol? named) ? new ClassType(named, []) : null);
					bases.Add(resolved);
				}
				catch (FormatException)
				{
					// Неизвестный родитель не должен валить импорт: у нативного типа
					// просто не будет этой базы (диагностика не нужна, spec §16).
				}
			}

			if (bases.Count > 0)
				child.SetBases(bases);
		}
	}

	private static IReadOnlyList<string> DefaultGenericNames(int arity) =>
		arity == 1 ? ["T"] : [.. Enumerable.Range(1, arity).Select(index => $"T{index}")];

	/// <summary>
	/// Статические нативные члены (<c>[KernStatic]</c>) раскладываются в
	/// <see cref="TypeSymbol.Members"/> своего типа: <c>datetime.now()</c> ищется обычным
	/// путём «вызов через имя типа» (spec §8), а не как свободная функция модуля.
	/// </summary>
	private static void AttachStaticMembers(
		Dictionary<string, TypeSymbol> types,
		IReadOnlyList<FunctionSymbol> natives,
		List<string> problems)
	{
		foreach (var group in natives
			.Where(function => function.NativeOwnerType is ClassType)
			.GroupBy(function => ((ClassType)function.NativeOwnerType!).Symbol.Name, StringComparer.Ordinal))
		{
			if (!types.TryGetValue(group.Key, out TypeSymbol? symbol) || symbol.Kind != SymbolKind.Native)
			{
				problems.Add($"нативный тип '{group.Key}': статические члены без символа типа");
				continue;
			}

			var members = new Dictionary<string, List<FunctionSymbol>>(StringComparer.Ordinal);
			foreach (FunctionSymbol function in group)
			{
				if (!members.TryGetValue(function.Name, out List<FunctionSymbol>? overloads))
					members[function.Name] = overloads = [];
				overloads.Add(function);
			}

			symbol.Members = MemberTable.Create(members, [], [], [], [], [], [], null);
		}
	}

	private static bool IsBuiltinName(string name) =>
		TypeCatalog.TryGetPrimitiveKind(name, out _) || TypeCatalog.TryGetBuiltin(name, out _);

	private static bool TryBuild(
		IReadOnlyDictionary<string, TypeSymbol> types,
		NativeFunctionDescriptor descriptor,
		out FunctionSymbol symbol,
		out string? error)
	{
		try
		{
			var variables = new Dictionary<string, TypeParameterType>(StringComparer.Ordinal);
			var genericParameters = new List<TypeParameterType>(descriptor.GenericParameters.Count);
			foreach (string name in descriptor.GenericParameters)
			{
				var parameter = new TypeParameterType(name, []);
				genericParameters.Add(parameter);
				variables[name] = parameter;
			}

			KernType Parse(string text) => NativeTypeParser.Parse(
				text,
				variables,
				name => types.TryGetValue(name, out TypeSymbol? named) ? new ClassType(named, []) : null);

			KernType? self = descriptor.SelfType is null ? null : Parse(descriptor.SelfType);
			KernType? owner = null;
			if (descriptor.IsStaticMember)
			{
				if (!types.TryGetValue(descriptor.Owner, out TypeSymbol? ownerSymbol))
					throw new FormatException($"владелец статического члена '{descriptor.Name}' не найден");
				owner = new ClassType(ownerSymbol, []);
			}

			var parameters = new List<ParameterSymbol>(descriptor.Parameters.Count);
			foreach (NativeParameterDescriptor parameter in descriptor.Parameters)
			{
				parameters.Add(new ParameterSymbol(
					parameter.Name,
					Parse(parameter.Type),
					DefaultValue: null,
					IsAutoInit: false,
					IsVararg: false,
					Span: default));
			}

			FunctionKind kind = descriptor.Operator is not null
				? FunctionKind.Operator
				: descriptor.Kind switch
				{
					Vm.Native.NativeMemberKind.PropertyGet => FunctionKind.Getter,
					Vm.Native.NativeMemberKind.PropertySet => FunctionKind.Setter,
					Vm.Native.NativeMemberKind.Constructor => FunctionKind.Constructor,
					_ => FunctionKind.Function,
				};

			KernType returnType = Parse(descriptor.ReturnType);
			// Фабрика generic-контейнера (`set<T>()`, `observable<T>()`) возвращает сам
			// контейнер: CLR-мост отдаёт `object`, а binder подставляет аргументы типа.
			if (kind == FunctionKind.Constructor && descriptor.SelfType is null && !descriptor.OwnerIsModule &&
				genericParameters.Count > 0)
			{
				if (TypeCatalog.TryGetBuiltin(descriptor.Owner, out BuiltinGenericKind builtin))
					returnType = new BuiltinGenericType(builtin, [.. genericParameters]);
				else if (types.TryGetValue(descriptor.Owner, out TypeSymbol? ownerType) &&
					ownerType.Kind == SymbolKind.Native && ownerType.GenericParameters.Count > 0)
					returnType = new ClassType(ownerType, [.. genericParameters]);
			}

			symbol = new FunctionSymbol(descriptor.Name, kind)
			{
				GenericParameters = genericParameters,
				Parameters = parameters,
				ReturnType = returnType,
				ReceiverType = self,
				NativeSelfType = self,
				NativeOwnerType = owner,
				NativeModuleName = descriptor.OwnerIsModule ? descriptor.Owner : null,
				NativeKey = descriptor.NativeKey,
				IsStatic = descriptor.IsStatic,
				OpSymbol = descriptor.Operator,
			};
			error = null;
			return true;
		}
		catch (FormatException exception)
		{
			symbol = null!;
			error = exception.Message;
			return false;
		}
	}
}
