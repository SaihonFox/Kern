using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Declarations;

/// <summary>Неработающий выбор родителя: код, сообщение и место объявления выбора.</summary>
public sealed record ParentSelectionProblem(string Code, string Message, TextSpan Span);

/// <summary>
/// Члены одного типа: методы, сгруппированные по имени для перегрузок, поля, свойства,
/// конструкторы, операторы и выборы родителей (spec §15). Наследование ленивое: таблица хранит
/// символы прямых родителей и при первом обращении сливает их видимые экземплярные члены
/// (slice A3a). Собственный член важнее унаследованного; выбор <c>$[A].member</c> снимает
/// неоднозначность и открывает член конкретного родителя.
/// </summary>
public sealed class MemberTable
{
	private readonly Dictionary<string, IReadOnlyList<FunctionSymbol>> _functions;
	private readonly IReadOnlyList<FieldSymbol> _fields;
	private readonly IReadOnlyList<PropertySymbol> _properties;
	private IReadOnlyList<TypeSymbol> _inheritedBases = [];
	private Dictionary<string, FieldSymbol>? _selectedFields;
	private Dictionary<string, FunctionSymbol>? _selectedFunctions;
	private IReadOnlyList<ParentSelectionProblem>? _selectionProblems;
	private bool _selectionsResolved;
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
	/// Прямые родители-классы, члены которых видны в этом типе (A3a: все, у <c>unit</c> пусто).
	/// Статические члены и конструкторы не наследуются.
	/// </summary>
	internal void SetInheritedBases(IReadOnlyList<TypeSymbol> bases)
	{
		_inheritedBases = bases;
		_selectedFields = null;
		_selectedFunctions = null;
		_selectionProblems = null;
		_selectionsResolved = false;
		ClearMerged();
	}

	public IReadOnlyList<FunctionSymbol> Constructors { get; }

	public IReadOnlyList<FunctionSymbol> Operators { get; }

	/// <summary>Выборы методов родителя <c>fun $[A].foo(...)</c>.</summary>
	public IReadOnlyList<FunctionSymbol> ParentSelections { get; }

	/// <summary>Выборы полей родителя <c>str $[A].name</c>.</summary>
	public IReadOnlyList<FieldSymbol> ParentFieldSelections { get; }

	/// <summary>Синтезированная функция инициализации статических полей, если она нужна.</summary>
	public FunctionSymbol? StaticInitializer { get; }

	/// <summary>
	/// Проверяет и разрешает выборы родителей. Вызывается сборщиком объявлений после того, как
	/// собраны члены всех типов модуля (ссылки вперёд), и повторно безопасен.
	/// </summary>
	internal IReadOnlyList<ParentSelectionProblem> ResolveParentSelections()
	{
		EnsureSelectionsResolved();
		return _selectionProblems ?? [];
	}

	/// <summary>Выбранный член родителя — реализация члена этого типа (для статической привязки).</summary>
	public bool IsParentSelection(FunctionSymbol function)
	{
		EnsureSelectionsResolved();
		if (_selectedFunctions is null)
			return false;

		foreach (var selected in _selectedFunctions.Values)
			if (ReferenceEquals(selected, function))
				return true;

		return false;
	}

	/// <summary>
	/// Имена прямых родителей, каждый из которых объявляет поле или свойство <paramref name="name"/>:
	/// больше одного — неоднозначность без выбора родителя. Пустой список — конфликта нет.
	/// </summary>
	public IReadOnlyList<string> MemberConflictOwners(string name)
	{
		if (_inheritedBases.Count < 2)
			return [];
		if (_fields.Any(field => field.Name == name) || _properties.Any(property => property.Name == name))
			return [];
		EnsureSelectionsResolved();
		if (_selectedFields is not null && _selectedFields.ContainsKey(name))
			return [];

		var owners = new List<string>(2);
		foreach (var type in _inheritedBases)
		{
			var field = type.Members.FindField(name);
			var property = type.Members.FindProperty(name);
			if (field is { IsStatic: false } || property is { IsStatic: false })
				owners.Add(type.Name);
		}

		return owners.Count > 1 ? owners : [];
	}

	public IReadOnlyList<FunctionSymbol> GetFunctions(string name)
	{
		if (_functions.TryGetValue(name, out var own))
			return own;
		if (_inheritedBases.Count == 0)
			return [];

		return Functions.TryGetValue(name, out var group) ? group : [];
	}

	public FunctionSymbol? FindFunction(string name, string signature) =>
		GetFunctions(name).FirstOrDefault(function => function.Signature == signature);

	public FieldSymbol? FindField(string name)
	{
		var own = _fields.FirstOrDefault(field => field.Name == name);
		if (own is not null)
			return own;

		EnsureSelectionsResolved();
		if (_selectedFields is not null && _selectedFields.TryGetValue(name, out var selected))
			return selected;
		if (_inheritedBases.Count == 0)
			return null;

		FieldSymbol? found = null;
		foreach (var type in _inheritedBases)
		{
			var candidate = type.Members.FindField(name);
			if (candidate is null || candidate.IsStatic)
				continue;
			// Одноимённые поля разных родителей неоднозначны: нужен $[A].name.
			if (found is not null && !ReferenceEquals(found, candidate))
				return null;
			found = candidate;
		}

		return found;
	}

	public PropertySymbol? FindProperty(string name)
	{
		var own = _properties.FirstOrDefault(property => property.Name == name);
		if (own is not null)
			return own;
		if (_inheritedBases.Count == 0)
			return null;

		PropertySymbol? found = null;
		foreach (var type in _inheritedBases)
		{
			var candidate = type.Members.FindProperty(name);
			if (candidate is null || candidate.IsStatic)
				continue;
			if (found is not null && !ReferenceEquals(found, candidate))
				return null;
			found = candidate;
		}

		return found;
	}

	private void ClearMerged()
	{
		_mergedFields = null;
		_mergedProperties = null;
		_mergedFunctions = null;
	}

	/// <summary>
	/// Разрешение выборов родителей в реальные символы членов (для codegen важна идентичность:
	/// слоты полей и байткод функций привязаны к исходным символам). Неразрешённые выборы
	/// возвращаются как проблемы — их печатает сборщик объявлений.
	/// </summary>
	private void EnsureSelectionsResolved()
	{
		if (_selectionsResolved)
			return;
		_selectionsResolved = true;
		var problems = new List<ParentSelectionProblem>();
		var selectedFields = new Dictionary<string, FieldSymbol>(StringComparer.Ordinal);
		var selectedFunctions = new Dictionary<string, FunctionSymbol>(StringComparer.Ordinal);

		foreach (var selection in ParentFieldSelections)
		{
			var span = selection.Declaration?.Span ?? default;
			if (FindAncestorSymbol(selection.ParentSelector) is not { } ancestor)
			{
				problems.Add(new ParentSelectionProblem(
					"KRN2103",
					$"Выбор родителя: тип '{selection.ParentSelector}' не является предком типа '{selection.Owner?.Name}'",
					span));
				continue;
			}

			var field = ancestor.Members.FindField(selection.Name);
			if (field is null)
			{
				problems.Add(new ParentSelectionProblem(
					"KRN2103",
					$"У родителя '{ancestor.Name}' нет поля '{selection.Name}' (выбор '$[{ancestor.Name}].{selection.Name}')",
					span));
				continue;
			}

			if (!field.Type.Equals(selection.Type))
			{
				problems.Add(new ParentSelectionProblem(
					"KRN2103",
					$"Поле '{ancestor.Name}.{selection.Name}' имеет тип '{field.Type}', а в выборе указан '{selection.Type}'",
					span));
				continue;
			}

			selectedFields[selection.Name] = field;
		}

		foreach (var selection in ParentSelections)
		{
			var span = selection.Declaration?.Span ?? default;
			if (FindAncestorSymbol(selection.ParentSelector) is not { } ancestor)
			{
				problems.Add(new ParentSelectionProblem(
					"KRN2103",
					$"Выбор родителя: тип '{selection.ParentSelector}' не является предком типа '{selection.Owner?.Name}'",
					span));
				continue;
			}

			FunctionSymbol? match = null;
			foreach (var candidate in ancestor.Members.GetFunctions(selection.Name))
				if (!candidate.IsStatic && candidate.Signature == selection.Signature)
				{
					match = candidate;
					break;
				}

			if (match is null)
			{
				problems.Add(new ParentSelectionProblem(
					"KRN2103",
					$"У родителя '{ancestor.Name}' нет метода '{selection.Signature}' (выбор '$[{ancestor.Name}].{selection.Name}')",
					span));
				continue;
			}

			selectedFunctions[selection.Signature] = match;
		}

		_selectedFields = selectedFields;
		_selectedFunctions = selectedFunctions;
		_selectionProblems = problems;
		ClearMerged();
	}

	/// <summary>Поиск предка по имени среди всех прямых родителей (включая самих родителей).</summary>
	private TypeSymbol? FindAncestorSymbol(string? name)
	{
		if (string.IsNullOrEmpty(name))
			return null;

		var visited = new HashSet<TypeSymbol>(ReferenceEqualityComparer.Instance);
		foreach (var baseType in _inheritedBases)
			if (FindSymbol(baseType, name, visited) is { } found)
				return found;

		return null;
	}

	private static TypeSymbol? FindSymbol(TypeSymbol symbol, string name, HashSet<TypeSymbol> visited)
	{
		if (!visited.Add(symbol))
			return null;
		if (symbol.Name == name)
			return symbol;

		foreach (var baseType in symbol.Bases)
			if (baseType is ClassType @class && FindSymbol(@class.Symbol, name, visited) is { } found)
				return found;

		return null;
	}

	/// <summary>
	/// Видимые унаследованные поля: для каждого родителя берётся его собственный или уникальный
	/// унаследованный член с этим именем (перекрытое поле родителя не всплывает).
	/// </summary>
	private IReadOnlyList<FieldSymbol> MergeFields()
	{
		if (_inheritedBases.Count == 0)
			return _fields;

		EnsureSelectionsResolved();
		var merged = new List<FieldSymbol>(_fields.Count + 8);
		var seen = new HashSet<FieldSymbol>(ReferenceEqualityComparer.Instance);
		foreach (var type in _inheritedBases)
			foreach (var field in type.Members.Fields)
			{
				if (field.IsStatic || field.IsBackingField)
					continue;
				if (!ReferenceEquals(type.Members.FindField(field.Name), field))
					continue;
				if (seen.Add(field))
					merged.Add(field);
			}

		if (_selectedFields is not null)
			foreach (var field in _selectedFields.Values)
				if (seen.Add(field))
					merged.Add(field);

		merged.AddRange(_fields);
		return merged;
	}

	private IReadOnlyList<PropertySymbol> MergeProperties()
	{
		if (_inheritedBases.Count == 0)
			return _properties;

		var merged = new List<PropertySymbol>(_properties.Count + 4);
		var seen = new HashSet<PropertySymbol>(ReferenceEqualityComparer.Instance);
		foreach (var type in _inheritedBases)
			foreach (var property in type.Members.Properties)
			{
				if (property.IsStatic)
					continue;
				if (!ReferenceEquals(type.Members.FindProperty(property.Name), property))
					continue;
				if (seen.Add(property))
					merged.Add(property);
			}

		merged.AddRange(_properties);
		return merged;
	}

	private IReadOnlyDictionary<string, IReadOnlyList<FunctionSymbol>> MergeFunctions()
	{
		if (_inheritedBases.Count == 0)
			return _functions;

		EnsureSelectionsResolved();
		var names = new HashSet<string>(_functions.Keys, StringComparer.Ordinal);
		foreach (var type in _inheritedBases)
			names.UnionWith(type.Members.Functions.Keys);
		if (_selectedFunctions is not null)
			foreach (var selected in _selectedFunctions.Values)
				names.Add(selected.Name);

		var merged = new Dictionary<string, IReadOnlyList<FunctionSymbol>>(StringComparer.Ordinal);
		foreach (var name in names)
		{
			var group = MergeFunctionGroup(name);
			if (group.Count > 0)
				merged[name] = group;
		}

		return merged;
	}

	/// <summary>
	/// Группа перегрузок с именем <paramref name="name"/>: собственные функции важнее унаследованных;
	/// иначе — выбранные родительские функции плюс все унаследованные. Одинаковые сигнатуры из
	/// разных родителей НЕ отбрасываются: неприменимость выбора родителей обнаруживает разрешение
	/// перегрузок (<c>KRN1402</c>).
	/// </summary>
	private IReadOnlyList<FunctionSymbol> MergeFunctionGroup(string name)
	{
		if (_functions.TryGetValue(name, out var own))
			return own;
		if (_inheritedBases.Count == 0)
			return [];

		var result = new List<FunctionSymbol>(4);
		var seen = new HashSet<FunctionSymbol>(ReferenceEqualityComparer.Instance);
		var selectedSignatures = new HashSet<string>(StringComparer.Ordinal);
		if (_selectedFunctions is not null)
			foreach (var selected in _selectedFunctions.Values)
			{
				if (selected.Name != name)
					continue;
				selectedSignatures.Add(selected.Signature);
				if (seen.Add(selected))
					result.Add(selected);
			}

		foreach (var type in _inheritedBases)
			foreach (var function in type.Members.GetFunctions(name))
			{
				if (function.IsStatic)
					continue;
				// Выбор родителя фиксирует именно эту реализацию: дубликат из другого родителя скрыт.
				if (selectedSignatures.Contains(function.Signature))
					continue;
				if (seen.Add(function))
					result.Add(function);
			}

		return result;
	}

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
