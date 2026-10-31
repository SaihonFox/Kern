using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Semantics.Declarations;

/// <summary>
/// Члены одного типа: методы, сгруппированные по имени для перегрузок, поля, свойства,
/// конструкторы, операторы и выборы родителей (spec §15).
/// </summary>
public sealed class MemberTable
{
	private readonly Dictionary<string, IReadOnlyList<FunctionSymbol>> _functions;

	private MemberTable(
		Dictionary<string, IReadOnlyList<FunctionSymbol>> functions,
		IReadOnlyList<FieldSymbol> fields,
		IReadOnlyList<PropertySymbol> properties,
		IReadOnlyList<FunctionSymbol> constructors,
		IReadOnlyList<FunctionSymbol> operators,
		IReadOnlyList<FunctionSymbol> parentSelections,
		IReadOnlyList<FieldSymbol> parentFieldSelections,
		FunctionSymbol? staticInitializer)
	{
		_functions = functions;
		Fields = fields;
		Properties = properties;
		Constructors = constructors;
		Operators = operators;
		ParentSelections = parentSelections;
		ParentFieldSelections = parentFieldSelections;
		StaticInitializer = staticInitializer;
	}

	public static MemberTable Empty { get; } = new(
		new Dictionary<string, IReadOnlyList<FunctionSymbol>>(StringComparer.Ordinal),
		[], [], [], [], [], [], null);

	public IReadOnlyDictionary<string, IReadOnlyList<FunctionSymbol>> Functions => _functions;

	public IReadOnlyList<FieldSymbol> Fields { get; }

	public IReadOnlyList<PropertySymbol> Properties { get; }

	public IReadOnlyList<FunctionSymbol> Constructors { get; }

	public IReadOnlyList<FunctionSymbol> Operators { get; }

	/// <summary>Выборы методов родителя <c>fun $[A].foo(...)</c>.</summary>
	public IReadOnlyList<FunctionSymbol> ParentSelections { get; }

	/// <summary>Выборы полей родителя <c>str $[A].name</c>.</summary>
	public IReadOnlyList<FieldSymbol> ParentFieldSelections { get; }

	/// <summary>Синтезированная функция инициализации статических полей, если она нужна.</summary>
	public FunctionSymbol? StaticInitializer { get; }

	public IReadOnlyList<FunctionSymbol> GetFunctions(string name) =>
		_functions.TryGetValue(name, out var group) ? group : [];

	public FunctionSymbol? FindFunction(string name, string signature) =>
		GetFunctions(name).FirstOrDefault(function => function.Signature == signature);

	public FieldSymbol? FindField(string name) =>
		Fields.FirstOrDefault(field => field.Name == name);

	public PropertySymbol? FindProperty(string name) =>
		Properties.FirstOrDefault(property => property.Name == name);

	internal static MemberTable Create(
		IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
		IReadOnlyList<FieldSymbol> fields,
		IReadOnlyList<PropertySymbol> properties,
		IReadOnlyList<FunctionSymbol> constructors,
		IReadOnlyList<FunctionSymbol> operators,
		IReadOnlyList<FunctionSymbol> parentSelections,
		IReadOnlyList<FieldSymbol> parentFieldSelections,
		FunctionSymbol? staticInitializer)
	{
		var grouped = new Dictionary<string, IReadOnlyList<FunctionSymbol>>(StringComparer.Ordinal);
		foreach (var (name, group) in functions)
			grouped[name] = [.. group];

		return new MemberTable(grouped, [.. fields], [.. properties], [.. constructors], [.. operators],
			[.. parentSelections], [.. parentFieldSelections], staticInitializer);
	}
}
