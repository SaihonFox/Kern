using Kern.Binding.Catalog;

namespace Kern.Compiler.Semantics.Symbols;

/// <summary>Нативный тип, доступный в области видимости под именем (после `use kern.text[str]` / `use dotnet.X[File]`).</summary>
public sealed class NativeTypeBinding(string name, NativeTypeSymbol native) : Symbol(name)
{
	public override SymbolKind Kind => SymbolKind.NativeType;
	public NativeTypeSymbol Native { get; } = native;
	public override string FullName => Native.FullName;
}

/// <summary>
/// Имя модуля/пространства имён после `use a.b` (без списка): доступ через `b.Имя`.
/// Для пользовательского модуля заполнен <see cref="Module"/>, для нативного — <see cref="NativeModule"/>,
/// для dotnet-пространства — только <see cref="Path"/>.
/// </summary>
public sealed class ModuleAliasSymbol(string name, string path) : Symbol(name)
{
	public override SymbolKind Kind => SymbolKind.ModuleAlias;
	public string Path { get; } = path;
	public ModuleSymbol? Module { get; init; }
	public NativeModule? NativeModule { get; init; }
	public override string FullName => Path;
}

/// <summary>Группа перегрузок, собранная из нескольких импортированных групп (живое объединение).</summary>
public sealed class ImportedOverloadSet(string name, IReadOnlyList<OverloadSet> sources) : OverloadSet(name)
{
	private readonly List<OverloadSet> _sources = [.. sources];
	public void AddSource(OverloadSet s)
	{
		if (!_sources.Contains(s)) _sources.Add(s);
	}
	public IReadOnlyList<OverloadSet> Sources => _sources;
	public override IReadOnlyList<FunctionSymbol> Functions => [.. _sources.SelectMany(s => s.Functions).Distinct()];
}
