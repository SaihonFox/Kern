using Kern.Compiler.Semantics.Types;
using Kern.Core;

namespace Kern.Compiler.Semantics.Bound;

/// <summary>
/// Базовый узел типизированного дерева (результат привязки AST к символам и типам).
/// Иммутабелен (record): оптимизатор переписывает дерево через <c>with</c>.
/// У каждого узла есть <see cref="Type"/> и <see cref="Span"/> (диапазон исходного узла AST в файле объемлющей функции).
/// </summary>
public abstract record BoundNode(TypeSymbol Type, TextSpan Span)
{
	/// <summary>Прямые дочерние узлы в порядке объявления свойств.</summary>
	public IEnumerable<BoundNode> Children() => BoundWalker.Children(this);
}

/// <summary>Выражение. <see cref="BoundNode.Type"/> — статический тип значения (после подстановки generic-аргументов).</summary>
public abstract record BoundExpr(TypeSymbol Type, TextSpan Span) : BoundNode(Type, Span)
{
	public bool IsError => Type.IsError;
}

/// <summary>
/// Инструкция внутри блока. Тип всегда <c>void</c>. Объявление переменной в позиции выражения
/// биндер оборачивает в блок: <c>{ decl; local }</c>.
/// </summary>
public abstract record BoundStmt(TextSpan Span) : BoundNode(PrimitiveType.Void, Span);

/// <summary>Паттерн. <see cref="BoundNode.Type"/> — тип проверяемого (входного) значения, к которому применяется паттерн.</summary>
public abstract record BoundPattern(TypeSymbol Type, TextSpan Span) : BoundNode(Type, Span);
