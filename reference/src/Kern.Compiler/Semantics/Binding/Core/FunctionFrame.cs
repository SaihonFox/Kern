using Kern.Compiler.Semantics.Scopes;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;

namespace Kern.Compiler.Semantics.Binding.Core;

/// <summary>Захваченная внешняя переменная (накапливается при привязке тела лямбды).</summary>
public sealed class CaptureInfo(Symbol variable, TypeSymbol type, TextSpan firstUse)
{
	public Symbol Variable { get; } = variable;
	public TypeSymbol Type { get; } = type;
	public TextSpan FirstUse { get; } = firstUse;
	/// <summary>Переменная присваивается внутри лямбды.</summary>
	public bool WrittenInside { get; set; }
}

/// <summary>
/// Контекст возврата и захвата одной функции или лямбды: что считается `ret`, какой тип возвращать,
/// какие внешние переменные использует тело. Лямбды образуют цепочку через <see cref="Parent"/>.
/// </summary>
public sealed class FunctionFrame(FunctionFrame? parent, bool isLambda)
{
	public FunctionFrame? Parent { get; } = parent;
	public bool IsLambda { get; } = isLambda;
	public bool IsAsync { get; init; }

	/// <summary>Объявленный (или уже выведенный) возвращаемый тип тела; null — выводится по `ret` и значению тела.</summary>
	public TypeSymbol? ReturnType { get; set; }

	/// <summary>Режим вывода: типы значений `ret` накапливаются в <see cref="InferredReturns"/>.</summary>
	public bool IsInferring => ReturnType is null;

	/// <summary>Типы значений всех `ret` (Void для `ret` без значения) в режиме вывода.</summary>
	public List<TypeSymbol> InferredReturns { get; } = [];

	/// <summary>Корневая область функции: параметры и generic-параметры.</summary>
	public Scope? RootScope { get; set; }

	public Dictionary<Symbol, CaptureInfo> Captures { get; } = [];
	public bool CapturesThis { get; set; }

	/// <summary>Лямбда/функция по правилам языка: `ret` допустим всегда внутри кадра.</summary>
	public string Description { get; init; } = "функция";
}
