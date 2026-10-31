using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Semantics.Declarations;

/// <summary>Расширения одного типа-получателя (spec §10). Ключ реестра — канонический текст типа.</summary>
public sealed record ExtensionGroup(
	KernType ReceiverType,
	IReadOnlyList<FunctionSymbol> Functions,
	IReadOnlyList<PropertySymbol> Properties)
{
	public IReadOnlyList<FunctionSymbol> GetFunctions(string name) =>
		[.. Functions.Where(function => function.Name == name)];

	public PropertySymbol? FindProperty(string name) =>
		Properties.FirstOrDefault(property => property.Name == name);
}
