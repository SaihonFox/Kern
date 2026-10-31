using Kern.Core.Diagnostics;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Semantics.Bound;

/// <summary>Оператор типизированного дерева.</summary>
public abstract record BoundStatement(TextSpan Span) : BoundNode(Span);

/// <summary>Блок операторов; <see cref="FallsThrough"/> учитывает завершающий <c>ret</c>/ветвление.</summary>
public sealed record BoundBlock(
	IReadOnlyList<BoundStatement> Statements,
	BoundExpression? Value,
	bool FallsThrough,
	TextSpan Span) : BoundStatement(Span);

/// <summary>Дополнительная привязка в multi-declare (<c>i32 a = 1, b = 2</c>).</summary>
public sealed record BoundLocalBinding(string Name, int Slot, BoundExpression? Initializer);

/// <summary>Объявление локальной переменной (после вывода типа).</summary>
public sealed record BoundLocalDeclaration(
	string Name,
	int Slot,
	KernType Type,
	BoundExpression? Initializer,
	bool IsSingleAssignment,
	TextSpan Span,
	IReadOnlyList<BoundLocalBinding>? AdditionalBindings = null) : BoundStatement(Span)
{
	public IReadOnlyList<BoundLocalBinding> DeclaredBindings { get; } = AdditionalBindings ?? [];
}

/// <summary>
/// <c>use T x = expr</c>: локальная переменная, освобождаемая вызовом <c>x.dispose()</c>
/// при выходе из блока (обычное завершение, <c>ret</c>, <c>stop</c>, <c>next</c>; при
/// панике очистка не выполняется — spec §12). <see cref="Dispose"/> = <c>null</c> —
/// тип не имеет <c>dispose()</c>; диагностика уже выдана, кодогенерация вызов пропускает.
/// </summary>
public sealed record BoundUseStatement(
	string Name,
	int Slot,
	KernType Type,
	BoundExpression Initializer,
	FunctionSymbol? Dispose,
	TextSpan Span) : BoundStatement(Span);

/// <summary>Присваивание локальной переменной; цель уже проверена.</summary>
public sealed record BoundAssignment(
	string Name,
	int Slot,
	KernType Type,
	BoundExpression Value,
	TextSpan Span) : BoundStatement(Span);

/// <summary>Присваивание полю; <see cref="Receiver"/> = null означает неявный <c>$</c>.</summary>
public sealed record BoundFieldAssignment(
	BoundExpression? Receiver,
	FieldSymbol Field,
	KernType Type,
	BoundExpression Value,
	TextSpan Span) : BoundStatement(Span);

public sealed record BoundExpressionStatement(
	BoundExpression Expression,
	TextSpan Span) : BoundStatement(Span);

/// <summary>
/// <c>_ = expr</c>: выражение вычисляется ради побочных эффектов, значение отбрасывается
/// без предупреждения KRN2305. <c>_</c> не объявляет переменную.
/// </summary>
public sealed record BoundDiscardStatement(
	BoundExpression Value,
	TextSpan Span) : BoundStatement(Span);

public sealed record BoundReturn(
	BoundExpression? Expression,
	TextSpan Span) : BoundStatement(Span);

/// <summary>
/// Цикл в позиции оператора: результат-список отбрасывается. Condition = null — бесконечный цикл;
/// <see cref="IsDoWhile"/> — условие проверяется после тела.
/// </summary>
public sealed record BoundLoopStatement(
	BoundExpression? Condition,
	BoundBlock Body,
	bool IsDoWhile,
	bool IsInfinite,
	TextSpan Span) : BoundStatement(Span)
{
	/// <summary>Метка <c>loop@outer</c>; null — цикл без метки.</summary>
	public string? Label { get; init; }
}

/// <summary>
/// Выход из ближайшего или помеченного цикла. <see cref="TargetDepth"/> — 0 для
/// ближайшего цикла, 1 для непосредственно объемлющего и т. д. Вне цикла binder
/// сообщает KRN2106.
/// </summary>
public sealed record BoundStopStatement(TextSpan Span) : BoundStatement(Span)
{
	public int TargetDepth { get; init; }
}

/// <summary>
/// Переход к следующей итерации ближайшего или помеченного цикла; глубина цели как у
/// <see cref="BoundStopStatement"/>.
/// </summary>
public sealed record BoundNextStatement(TextSpan Span) : BoundStatement(Span)
{
	public int TargetDepth { get; init; }
}
