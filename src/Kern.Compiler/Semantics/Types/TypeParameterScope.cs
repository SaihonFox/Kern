namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Неизменяемая область generic-параметров. <see cref="Add"/> возвращает новую область,
/// исходная не меняется.
/// </summary>
public sealed class TypeParameterScope
{
	private readonly Dictionary<string, TypeParameterType> _parameters;

	private TypeParameterScope(Dictionary<string, TypeParameterType> parameters) => _parameters = parameters;

	public static TypeParameterScope Empty { get; } = new([]);

	public int Count => _parameters.Count;

	public IReadOnlyCollection<string> Names => [.. _parameters.Keys];

	public TypeParameterScope Add(string name, IReadOnlyList<KernType>? constraints = null)
	{
		ArgumentNullException.ThrowIfNull(name);
		var parameters = new Dictionary<string, TypeParameterType>(_parameters, StringComparer.Ordinal)
		{
			[name] = new TypeParameterType(name, constraints ?? [])
		};
		return new TypeParameterScope(parameters);
	}

	public TypeParameterScope Add(TypeParameterType parameter)
	{
		ArgumentNullException.ThrowIfNull(parameter);
		var parameters = new Dictionary<string, TypeParameterType>(_parameters, StringComparer.Ordinal)
		{
			[parameter.Name] = parameter
		};
		return new TypeParameterScope(parameters);
	}

	public bool TryLookup(string name, out TypeParameterType parameter) =>
		_parameters.TryGetValue(name, out parameter!);

	public TypeParameterType? Lookup(string name) =>
		_parameters.TryGetValue(name, out var parameter) ? parameter : null;
}
