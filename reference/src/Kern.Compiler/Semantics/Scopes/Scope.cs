using Kern.Compiler.Semantics.Symbols;

namespace Kern.Compiler.Semantics.Scopes;

/// <summary>
/// Лексическая область: имя → символ. Функции хранятся группами <see cref="OverloadSet"/>;
/// внутренняя область скрывает одноимённое внешнее (как в C#).
/// </summary>
public sealed class Scope(Scope? parent, string description = "")
{
	private readonly Dictionary<string, Symbol> _symbols = [];

	public Scope? Parent { get; } = parent;
	public string Description { get; } = description;
	public IReadOnlyDictionary<string, Symbol> Symbols => _symbols;

	/// <summary>Только в этой области.</summary>
	public Symbol? LookupLocal(string name) => _symbols.GetValueOrDefault(name);

	/// <summary>По цепочке родителей; возвращает ближайшее объявление.</summary>
	public Symbol? Lookup(string name)
	{
		for (var s = this; s is not null; s = s.Parent)
			if (s._symbols.TryGetValue(name, out var sym)) return sym;
		return null;
	}

	/// <summary>Ближайшая группа перегрузок с этим именем (не-функции игнорируются).</summary>
	public OverloadSet? LookupFunctions(string name)
	{
		for (var s = this; s is not null; s = s.Parent)
			if (s._symbols.TryGetValue(name, out var sym) && sym is OverloadSet os) return os;
		return null;
	}

	/// <summary>Ближайший тип (пользовательский, нативный или generic-параметр) с этим именем.</summary>
	public Symbol? LookupType(string name)
	{
		for (var s = this; s is not null; s = s.Parent)
			if (s._symbols.TryGetValue(name, out var sym) && sym.Kind is SymbolKind.Type or SymbolKind.NativeType or SymbolKind.TypeParam) return sym;
		return null;
	}

	/// <summary>
	/// Объявляет символ. Возвращает уже существующий конфликтующий символ (тогда ничего не меняется), иначе null.
	/// Для функций используйте <see cref="DeclareFunction"/>.
	/// </summary>
	public Symbol? Declare(Symbol symbol)
	{
		if (_symbols.TryGetValue(symbol.Name, out var existing)) return existing;
		_symbols[symbol.Name] = symbol;
		return null;
	}

	/// <summary>Заменяет/добавляет запись без проверок (импорты, служебные символы).</summary>
	public void Set(Symbol symbol) => _symbols[symbol.Name] = symbol;

	/// <summary>Получает группу перегрузок имени, создавая её; null — имя занято не-функцией (она в <paramref name="conflict"/>).</summary>
	public OverloadSet? GetOrCreateGroup(string name, Symbol? container, out Symbol? conflict)
	{
		conflict = null;
		if (_symbols.TryGetValue(name, out var existing))
		{
			if (existing is OverloadSet os) return os;
			conflict = existing;
			return null;
		}
		var set = new OverloadSet(name) { Container = container };
		_symbols[name] = set;
		return set;
	}

	/// <summary>
	/// Добавляет перегрузку. <paramref name="duplicate"/> — перегрузка с той же сигнатурой,
	/// <paramref name="conflict"/> — не-функция с тем же именем; при любом из них функция не добавляется.
	/// </summary>
	public bool DeclareFunction(FunctionSymbol f, out FunctionSymbol? duplicate, out Symbol? conflict)
	{
		duplicate = null;
		var set = GetOrCreateGroup(f.Name, f.Container, out conflict);
		if (set is null) return false;
		duplicate = set.Add(f);
		return duplicate is null;
	}
}
