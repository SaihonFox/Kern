using Kern.Binding.Catalog;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Native;

/// <summary>
/// Превращает <see cref="NativeCatalog"/> в символы компилятора (spec §12.9):
/// top-level функции добавляются в <see cref="ModuleSymbol.Functions"/>, члены
/// встроенных типов — в <see cref="ModuleSymbol.NativeFunctions"/>.
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
		var natives = new List<FunctionSymbol>();
		foreach (NativeFunctionDescriptor descriptor in catalog.Functions)
		{
			if (descriptor.Method is not null && !descriptor.Method.IsStatic)
			{
				problems.Add($"native '{descriptor.NativeKey}': экземплярные члены пока не поддержаны");
				continue;
			}

			if (!TryBuild(module, descriptor, out FunctionSymbol? symbol, out string? error))
			{
				problems.Add($"native '{descriptor.NativeKey}': {error}");
				continue;
			}

			natives.Add(symbol);
		}

		var functions = new Dictionary<string, List<FunctionSymbol>>(StringComparer.Ordinal);
		foreach (var (name, group) in module.Functions)
			functions[name] = [.. group];

		foreach (FunctionSymbol native in natives)
		{
			if (native.NativeSelfType is not null)
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
		return module with { Functions = merged, NativeFunctions = natives };
	}

	private static bool TryBuild(
		ModuleSymbol module,
		NativeFunctionDescriptor descriptor,
		out FunctionSymbol? symbol,
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
				name => module.FindType(name) is { } named ? new ClassType(named, []) : null);

			KernType? self = descriptor.SelfType is null ? null : Parse(descriptor.SelfType);
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

			FunctionKind kind = descriptor.Kind switch
			{
				Vm.Native.NativeMemberKind.PropertyGet => FunctionKind.Getter,
				Vm.Native.NativeMemberKind.PropertySet => FunctionKind.Setter,
				Vm.Native.NativeMemberKind.Constructor => FunctionKind.Constructor,
				_ => FunctionKind.Function,
			};

			symbol = new FunctionSymbol(descriptor.Name, kind)
			{
				GenericParameters = genericParameters,
				Parameters = parameters,
				ReturnType = Parse(descriptor.ReturnType),
				ReceiverType = self,
				NativeSelfType = self,
				NativeKey = descriptor.NativeKey,
				IsStatic = descriptor.IsStatic,
			};
			error = null;
			return true;
		}
		catch (FormatException exception)
		{
			symbol = null;
			error = exception.Message;
			return false;
		}
	}
}
