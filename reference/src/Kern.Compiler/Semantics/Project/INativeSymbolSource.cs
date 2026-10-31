using Kern.Binding.Catalog;

namespace Kern.Compiler.Semantics.Project;

/// <summary>
/// Источник нативных символов для компилятора (Kern-модули `kern.*` и dotnet-типы).
/// Тонкий адаптер над каталогом (NativeCatalog.FindModule/FindType/GetMembers, DotnetCatalog).
/// </summary>
public interface INativeSymbolSource
{
	/// <summary>Нативный Kern-модуль по имени (`kern.text`, `kern.io`); null — нет такого.</summary>
	NativeModule? FindModule(string name);

	/// <summary>Тип модуля по имени (module может быть и `dotnet.System.IO`).</summary>
	NativeTypeSymbol? FindType(string module, string name);

	/// <summary>Любой публичный .NET-тип по пространству имён и простому имени (способ C).</summary>
	NativeTypeSymbol? FindDotnetType(string dotnetNamespace, string name);

	/// <summary>Существует ли такое .NET-пространство имён (для `use dotnet.System.IO`).</summary>
	bool DotnetNamespaceExists(string dotnetNamespace);

	/// <summary>Члены нативного типа.</summary>
	IReadOnlyList<NativeMember> GetMembers(NativeTypeSymbol type);
}

/// <summary>Пустой источник: нативных типов нет (тесты, анализ без рантайма).</summary>
public sealed class EmptyNativeSource : INativeSymbolSource
{
	public static readonly EmptyNativeSource Instance = new();
	public NativeModule? FindModule(string name) => null;
	public NativeTypeSymbol? FindType(string module, string name) => null;
	public NativeTypeSymbol? FindDotnetType(string dotnetNamespace, string name) => null;
	public bool DotnetNamespaceExists(string dotnetNamespace) => false;
	public IReadOnlyList<NativeMember> GetMembers(NativeTypeSymbol type) => type.Members;
}
