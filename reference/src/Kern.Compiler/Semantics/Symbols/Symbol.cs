using Kern.Compiler.Syntax;
using Kern.Core;

namespace Kern.Compiler.Semantics.Symbols;

public enum SymbolKind { Module, ModuleAlias, Type, NativeType, TypeParam, Function, OverloadSet, Property, Field, Parameter, Local }

/// <summary>Применение атрибута `#[A(args)]`: синтаксис + разрешённый attribute-тип (null — не найден).</summary>
public sealed record AttributeApplication(AttributeUse Syntax, TypeDeclSymbol? AttributeType);

/// <summary>Базовый символ: имя, владелец, место объявления.</summary>
public abstract class Symbol(string name)
{
	public string Name { get; } = name;
	public abstract SymbolKind Kind { get; }

	/// <summary>Объемлющий символ: модуль, тип, функция.</summary>
	public Symbol? Container { get; set; }
	public SourceFile? File { get; set; }
	/// <summary>Диапазон имени в исходнике.</summary>
	public TextSpan Span { get; set; }
	public Access Access { get; set; }
	public IReadOnlyList<AttributeApplication> Attributes { get; set; } = [];

	/// <summary>Полное имя: `модуль.Тип.член`.</summary>
	public virtual string FullName => Container switch
	{
		null => Name,
		ModuleSymbol { Name: "" } => Name,
		{ } c => $"{c.FullName}.{Name}",
	};

	/// <summary>Объемлющий тип (если символ — член типа).</summary>
	public TypeDeclSymbol? DeclaringType => Container as TypeDeclSymbol;

	public override string ToString() => FullName;
}

/// <summary>Группа перегрузок одного имени. Никакого слияния: каждая перегрузка — отдельный <see cref="FunctionSymbol"/>.</summary>
public class OverloadSet(string name) : Symbol(name)
{
	private readonly List<FunctionSymbol> _functions = [];
	public override SymbolKind Kind => SymbolKind.OverloadSet;
	public virtual IReadOnlyList<FunctionSymbol> Functions => _functions;

	/// <summary>Добавляет перегрузку. Возвращает уже существующую с той же сигнатурой (тогда ничего не добавляется).</summary>
	public FunctionSymbol? Add(FunctionSymbol f)
	{
		foreach (var e in _functions)
			if (e.HasSameSignature(f)) return e;
		_functions.Add(f);
		return null;
	}

	/// <summary>Добавляет без проверки дубликатов (для нативных импортов и аксессоров).</summary>
	public void AddUnchecked(FunctionSymbol f) => _functions.Add(f);
}
