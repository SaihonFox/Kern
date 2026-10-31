using Kern.Binding.Catalog;

namespace Kern.Compiler.Semantics.Project;

/// <summary>Адаптер <see cref="NativeCatalog"/> → <see cref="INativeSymbolSource"/>.</summary>
public sealed class CatalogNativeSource(NativeCatalog catalog) : INativeSymbolSource
{
	public NativeModule? FindModule(string name) => catalog.FindModule(name);
	public NativeTypeSymbol? FindType(string module, string name) => catalog.FindType(module, name);
	public NativeTypeSymbol? FindDotnetType(string dotnetNamespace, string name) => catalog.FindDotnetType(dotnetNamespace, name);
	public bool DotnetNamespaceExists(string dotnetNamespace) => catalog.DotnetNamespaceExists(dotnetNamespace);
	public IReadOnlyList<NativeMember> GetMembers(NativeTypeSymbol type) => catalog.GetMembers(type);
}
