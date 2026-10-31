using System.Reflection;
using Kern.Binding.Attributes;

namespace Kern.Binding.Catalog;

/// <summary>
/// C#-namespace → Kern-модуль. Источник: сборочные [assembly: KernModule("kern.text", "Kern.Runtime.Text")]
/// (все они собраны в Kern.Runtime/ModuleMap.cs). Побеждает самый длинный префикс namespace.
/// </summary>
public sealed class ModuleMapIndex
{
	public const string DefaultModule = "kern.core";
	private readonly List<(string Namespace, string Module)> _maps = [];

	public void AddAssembly(Assembly assembly)
	{
		foreach (var a in assembly.GetCustomAttributes<KernModuleAttribute>())
			if (a.Namespace is { Length: > 0 } ns && !_maps.Contains((ns, a.Name))) _maps.Add((ns, a.Name));
	}

	/// <summary>Kern-модуль C#-типа: [KernType(module:)] → [KernModule] на классе → карта namespace → "kern".</summary>
	public string ModuleOf(Type type)
	{
		var def = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
		if (def.GetCustomAttribute<KernTypeAttribute>()?.Module is { } m) return m;
		if (def.GetCustomAttribute<KernModuleAttribute>() is { } km) return km.Name;
		return ModuleOfNamespace(def.Namespace ?? def.DeclaringType?.Namespace ?? "");
	}

	public string ModuleOfNamespace(string ns)
	{
		string? best = null; int bestLen = -1;
		foreach (var (prefix, module) in _maps)
			if ((ns == prefix || ns.StartsWith(prefix + ".", StringComparison.Ordinal)) && prefix.Length > bestLen)
				(best, bestLen) = (module, prefix.Length);
		return best ?? DefaultModule;
	}
}
