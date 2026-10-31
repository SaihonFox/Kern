namespace Kern.Binding.Modules;

/// <summary>
/// Соответствие CLR-namespace → Kern-модуль (spec §12, `use kern.text`). Явные
/// регистрации имеют приоритет; по умолчанию отрезается `Kern.Runtime.` и остаток
/// переводится в нижний регистр (`Kern.Runtime.Core` → `kern.core`).
/// </summary>
public sealed class ModuleMap
{
	private readonly Dictionary<string, string> _explicit = new(StringComparer.Ordinal);

	public static ModuleMap Default { get; } = new();

	public ModuleMap Add(string clrNamespace, string kernModule)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(clrNamespace);
		ArgumentException.ThrowIfNullOrWhiteSpace(kernModule);
		_explicit[clrNamespace] = kernModule;
		return this;
	}

	public string? FromNamespace(string clrNamespace)
	{
		ArgumentNullException.ThrowIfNull(clrNamespace);
		if (_explicit.TryGetValue(clrNamespace, out string? module))
			return module;

		const string prefix = "Kern.Runtime.";
		if (clrNamespace.StartsWith(prefix, StringComparison.Ordinal))
			return "kern." + clrNamespace[prefix.Length..].Replace('.', '_').ToLowerInvariant();

		if (clrNamespace.StartsWith("Kern.Runtime", StringComparison.Ordinal))
			return "kern";

		return null;
	}
}
