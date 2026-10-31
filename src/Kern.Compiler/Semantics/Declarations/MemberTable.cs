using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Semantics.Declarations;

/// <summary>
/// Члены одного типа: методы, сгруппированные по имени для перегрузок, поля, свойства,
/// конструкторы, операторы и выборы родителей (spec §15).
/// </summary>
public sealed class MemberTable
{
	private readonly Dictionary<string, IReadOnlyList<FunctionSymbol>> _functions;
	private readonly IReadOnlyList<FieldSymbol> _fields;
	private readonly IReadOnlyList<PropertySymbol> _properties;
	private IReadOnlyList<TypeSymbol> _inheritedBases = [];
	private IReadOnlyList<FieldSymbol>? _mergedFields;
	private IReadOnlyList<PropertySymbol>? _mergedProperties;
	private IReadOnlyDictionary<string, IReadOnlyList<FunctionSymbol>>? _mergedFunctions;

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
		_fields = fields;
		_properties = properties;
		Constructors = constructors;
		Operators = operators;
		ParentSelections = parentSelections;
		ParentFieldSelections = parentFieldSelections;
		StaticInitializer = staticInitializer;
	}

	public static MemberTable Empty { get; } = new(
		new Dictionary<string, IReadOnlyList<FunctionSymbol>>(StringComparer.Ordinal),
		[], [], [], [], [], [], null);

	/// <summary>Встроенный тип с полями и без поведения (например, <c>Ok&lt;T&gt;</c>/<c>Err&lt;E&gt;</c>).</summary>
	public static MemberTable ForFields(IReadOnlyList<FieldSymbol> fields) => new(
		new Dictionary<string, IReadOnlyList<FunctionSymbol>>(StringComparer.Ordinal),
		fields,
		[], [], [], [], [], null);

	public IReadOnlyDictionary<string, IReadOnlyList<FunctionSymbol>> Functions => _mergedFunctions ??= MergeFunctions();

	/// <summary>Поля с учётом унаследованных; статические члены родителя не наследуются.</summary>
	public IReadOnlyList<FieldSymbol> Fields => _mergedFields ??= MergeFields();

	/// <summary>Собственные поля объявления (для раскладки слотов в TypeDef).</summary>
	internal IReadOnlyList<FieldSymbol> OwnFields => _fields;

	public IReadOnlyList<PropertySymbol> Properties => _mergedProperties ??= MergeProperties();

	/// <summary>Собственные свойства объявления (backing-поля участвуют в раскладке).</summary>
	internal IReadOnlyList<PropertySymbol> OwnProperties => _properties;

	/// <summary>
	/// Прямые родители-классы, члены которых видны в этом типе (slice A1: только первый).
	/// Статические члены и конструкторы не наследуются.
	/// </summary>
	internal void SetInheritedBases(IReadOnlyList<TypeSymbol> bases)
	{
		_inheritedBases = bases;
		_mergedFields = null;
		_mergedProperties = null;
		_mergedFunctions = null;
	}

	public IReadOnlyList<FunctionSymbol> Constructors { get; }

	public IReadOnlyList<FunctionSymbol> Operators { get; }

	/// <summary>Выборы методов родителя <c>fun $[A].foo(...)</c>.</summary>
	public IReadOnlyList<FunctionSymbol> ParentSelections { get; }

	/// <summary>Выборы полей родителя <c>str $[A].name</c>.</summary>
	public IReadOnlyList<FieldSymbol> ParentFieldSelections { get; }

	/// <summary>Синтезированная функция инициализации статических полей, если она нужна.</summary>
	public FunctionSymbol? StaticInitializer { get; }

	public IReadOnlyList<FunctionSymbol> GetFunctions(string name)
	{
		if (_inheritedBases.Count == 0)
			return _functions.TryGetValue(name, out var own) ? own : [];
		return Functions.TryGetValue(name, out var group) ? group : [];
	}

	private IReadOnlyList<FieldSymbol> MergeFields()
	{
		if (_inheritedBases.Count == 0)
			return _fields;

		var merged = new List<FieldSymbol>(_fields.Count + 8);
		foreach (var type in _inheritedBases)
			foreach (var field in type.Members.Fields)
				if (!field.IsStatic && !field.IsBackingField)
					merged.Add(field);
		merged.AddRange(_fields);
		return merged;
	}

	private IReadOnlyList<PropertySymbol> MergeProperties()
	{
		if (_inheritedBases.Count == 0)
			return _properties;

		var merged = new List<PropertySymbol>(_properties.Count + 4);
		foreach (var type in _inheritedBases)
			foreach (var property in type.Members.Properties)
				if (!property.IsStatic)
					merged.Add(property);
		merged.AddRange(_properties);
		return merged;
	}

	private IReadOnlyDictionary<string, IReadOnlyList<FunctionSymbol>> MergeFunctions()
	{
		if (_inheritedBases.Count == 0)
			return _functions;

		var merged = new Dictionary<string, IReadOnlyList<FunctionSymbol>>(StringComparer.Ordinal);
		var signatures = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
		foreach (var (name, group) in _functions)
		{
			merged[name] = group;
			signatures[name] = [.. group.Select(function => function.Signature)];
		}

		foreach (var type in _inheritedBases)
		{
			foreach (var (name, group) in type.Members.Functions)
			{
				var seen = signatures.TryGetValue(name, out var set) ? set : null;
				var additions = new List<FunctionSymbol>();
				foreach (var function in group)
				{
					if (function.IsStatic)
						continue;
					if (seen is not null && !seen.Add(function.Signature))
						continue;
					additions.Add(function);
				}

				if (additions.Count == 0)
					continue;
				if (seen is null)
				{
					seen = [.. additions.Select(function => function.Signature)];
					signatures[name] = seen;
				}

				merged[name] = merged.TryGetValue(name, out var existing)
					? [.. existing, .. additions]
					: additions;
			}
		}

		return merged;
	}

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
