using Kern.Core.Diagnostics;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;

namespace Kern.Compiler.Semantics.Bound;

/// <summary>Базовый узел типизированного дерева (M3).</summary>
public abstract record BoundNode(TextSpan Span);

/// <summary>Выражение с уже вычисленным типом.</summary>
public abstract record BoundExpression(KernType Type, TextSpan Span) : BoundNode(Span);

/// <summary>Литерал. <paramref name="Numeric"/> заполнен для чисел и нужен codegen/константным преобразованиям.</summary>
public sealed record BoundLiteralExpression(
	object? Value,
	NumericLiteralValue? Numeric,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);

/// <summary>Ссылка на параметр или локальную переменную с её слотом.</summary>
public sealed record BoundVariableExpression(
	string Name,
	int Slot,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);

/// <summary>
/// Аргумент с маркером <c>&amp;x</c>/<c>*x</c> (spec §6). <see cref="Mutable"/> = <c>*</c>:
/// кодогенерация передаёт ячейку на место вызова (локальную, поле или статическое поле);
/// <c>&amp;</c> передаёт значение только для чтения. Тип выражения — тип места.
/// </summary>
public sealed record BoundReferenceArgumentExpression(
	BoundExpression Operand,
	bool Mutable,
	TextSpan Span) : BoundExpression(Operand.Type, Span);

/// <summary>
/// Выбор родителя в выражении <c>$[A]</c> (spec §2): приёмник — обычный <c>$</c>
/// (<see cref="Self"/>), статический тип — выбранный родитель <see cref="ParentType"/>.
/// Доступ к членам через этот узел привязывается к реализации родителя статически,
/// без <c>CallVirtual</c>.
/// </summary>
public sealed record BoundSuperExpression(
	BoundExpression Self,
	KernType ParentType,
	TextSpan Span) : BoundExpression(ParentType, Span);

/// <summary>Явное значение <c>nil</c>; тип — <see cref="NilType"/>.</summary>
public sealed record BoundNilExpression(TextSpan Span) : BoundExpression(NilType.Instance, Span);

/// <summary>
/// Внутреннее состояние <c>none</c> неприсвоенной переменной; тип — <see cref="PrimitiveType.None"/>.
/// Читается только в сравнении <c>== none</c>/<c>!= none</c> и в проверке наличия значения.
/// </summary>
public sealed record BoundNoneExpression(TextSpan Span) : BoundExpression(PrimitiveType.None, Span);

/// <summary>
/// Проверка наличия значения: <c>true</c>, если операнд не <c>nil</c> и не <c>none</c>.
/// <see cref="Negated"/> соответствует <c>!x</c> (spec §4).
/// </summary>
public sealed record BoundTruthinessExpression(
	BoundExpression Operand,
	bool Negated,
	TextSpan Span) : BoundExpression(PrimitiveType.Bool, Span);

/// <summary>Проверка типа <c>x is T</c>/<c>x !is T</c> с правым операндом-типом.</summary>
public sealed record BoundTypeTestExpression(
	BoundExpression Operand,
	KernType TestType,
	bool Negated,
	TextSpan Span) : BoundExpression(PrimitiveType.Bool, Span);

public sealed record BoundBinaryExpression(
	BoundExpression Left,
	string Operator,
	BoundExpression Right,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);

/// <summary>
/// Цепочка сравнений <c>a &lt; b &lt; c</c>: эквивалентна
/// <c>a &lt; b &amp;&amp; b &lt; c</c>, но каждый операнд вычисляется один раз
/// (codegen хранит средние значения во временном слоте).
/// </summary>
public sealed record BoundComparisonChainExpression(
	IReadOnlyList<BoundExpression> Operands,
	IReadOnlyList<string> Operators,
	TextSpan Span) : BoundExpression(PrimitiveType.Bool, Span);

public sealed record BoundUnaryExpression(
	string Operator,
	BoundExpression Operand,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);

/// <summary>
/// Постфиксное утверждение «не <c>nil</c>»: тип <c>T?</c> сужается до <c>T</c>,
/// байткод — просто операнд (проверка происходит при использовании значения).
/// </summary>
public sealed record BoundNonNullExpression(
	BoundExpression Operand,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);

/// <summary>
/// <c>await task&lt;T&gt;</c> (spec §16): допустим только внутри <c>async fun</c>; результат —
/// <c>T</c>. Codegen печатает операнд и <c>Await</c>; VM припаркует состояние, если задача
/// ещё не завершена, иначе положит результат (паника задачи всплывает этим же оператором).
/// </summary>
public sealed record BoundAwaitExpression(
	BoundExpression Operand,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);

/// <summary>
/// Elvis <c>left ?: right</c> (spec §3/§4): <c>nil</c> и <c>none</c> слева означают
/// «значения нет» — результат <paramref name="Right"/>, иначе само значение слева.
/// Левая часть вычисляется ровно один раз.
/// </summary>
public sealed record BoundElvisExpression(
	BoundExpression Left,
	BoundExpression Right,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);

/// <summary>
/// <c>ret value</c> в правой ветви Elvis (parser §3): тип <c>never</c>; codegen печатает
/// значение, очистку <c>use</c>-кадров и <c>Ret</c>.
/// </summary>
public sealed record BoundReturnExpression(
	BoundExpression? Value,
	TextSpan Span) : BoundExpression(PrimitiveType.Never, Span);

/// <summary>
/// <c>obj?.member</c> (spec §3): приёмник вычисляется один раз и лежит в
/// <paramref name="Slot"/>; при <c>nil</c> результат — <c>nil</c>, иначе вычисляется
/// <paramref name="Access"/> (он читает приёмник из этого слота). Тип результата —
/// nullable, у <c>void</c>-вызова остаётся <c>void</c>.
/// </summary>
public sealed record BoundConditionalAccessExpression(
	BoundExpression Receiver,
	int Slot,
	BoundExpression Access,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);

/// <summary>Неявное преобразование, вставленное binder'ом (spec §9, §15).</summary>
public sealed record BoundConversionExpression(
	BoundExpression Operand,
	Conversion Conversion,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);

/// <summary>Блок как выражение: значение — <see cref="Value"/> (null для void).</summary>
public sealed record BoundBlockExpression(
	IReadOnlyList<BoundStatement> Statements,
	BoundExpression? Value,
	bool FallsThrough,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);

public sealed record BoundIfExpression(
	BoundExpression Condition,
	BoundExpression Then,
	BoundExpression? Else,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);

/// <summary>
/// Цикл как выражение: результат — <c>list&lt;T&gt;</c>, где T — тип значения тела.
/// <paramref name="Condition"/> = null для бесконечного цикла; <paramref name="IsDoWhile"/>
/// означает проверку условия после тела.
/// </summary>
public sealed record BoundLoopExpression(
	BoundExpression? Condition,
	BoundBlock Body,
	bool IsDoWhile,
	bool IsInfinite,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span)
{
	/// <summary>Слот накопителя <c>list&lt;T&gt;</c>; -1 — старый путь без списка.</summary>
	public int ListSlot { get; init; } = -1;

	/// <summary>Инкремент счётчика итерации, выполняется после добавления значения.</summary>
	public BoundStatement? Increment { get; init; }

	/// <summary>Метка <c>loop@outer</c>; null — цикл без метки.</summary>
	public string? Label { get; init; }
}

/// <summary>Вызов после разрешения перегрузки; аргументы идут в порядке параметров.</summary>
/// <summary>
/// Вызов функции/метода. <see cref="Receiver"/> заполнен для метода экземпляра:
/// codegen передаёт его первым аргументом (слот 0 = <c>$</c>).
/// </summary>
public sealed record BoundCallExpression(
	FunctionSymbol Function,
	IReadOnlyList<BoundExpression> Arguments,
	KernType Type,
	TextSpan Span,
	BoundExpression? Receiver = null) : BoundExpression(Type, Span)
{
	/// <summary>
	/// Метод экземпляра класса: codegen печатает <c>CallVirtual</c>, VM ищет ближайшее
	/// переопределение по типу получателя (spec §8). Конструкторы, нативные вызовы,
	/// расширения и методы <c>unit</c>/<c>unit!</c> остаются статическими.
	/// </summary>
	public bool Virtual { get; init; }

	/// <summary>
	/// Фактические аргументы generic-параметров вызова (выведенные или явные). Для нативных
	/// функций codegen создаёт отдельный <c>FunctionDef</c> с reified-аргументами
	/// (<c>NativeTypeArgs</c>), например <c>json.from_str&lt;Person&gt;</c>.
	/// </summary>
	public IReadOnlyList<KernType> TypeArguments { get; init; } = [];
}

/// <summary>
/// Встроенная индексация <c>xs[i]</c>/<c>d[k]</c> (B5b1, spec §3): цель — <c>list&lt;T&gt;</c>,
/// <c>array&lt;T&gt;</c> или <c>dict&lt;K, V&gt;</c>, индекс уже приведён к <c>i32</c> (для словаря —
/// к <c>K</c>). Отрицательный индекс и отсутствующий ключ обрабатывает VM (паника).
/// </summary>
public sealed record BoundIndexExpression(
	BoundExpression Target,
	BoundExpression Index,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);

/// <summary>
/// Срез <c>xs[start..end]</c>/<c>xs[start..=end]</c> (B5b2, spec §3, round 27): цель —
/// <c>list&lt;T&gt;</c>, <c>array&lt;T&gt;</c> или <c>str</c>, границы уже приведены к <c>i32</c>.
/// Результат — новый <c>list&lt;T&gt;</c> (для array тоже список) или новая <c>str</c>.
/// Отрицательные границы, включительный конец и проверку диапазона выполняет VM (паника).
/// </summary>
public sealed record BoundSliceExpression(
	BoundExpression Target,
	BoundExpression Start,
	BoundExpression? End,
	bool Inclusive,
	KernType Type,
	TextSpan Span,
	BoundExpression? Step = null) : BoundExpression(Type, Span);

/// <summary>Чтение поля; <see cref="Receiver"/> = null означает неявный <c>$</c>.</summary>
public sealed record BoundFieldExpression(
	BoundExpression? Receiver,
	FieldSymbol Field,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);

/// <summary>Создание unit/type: NewObject + вызов конструктора с <c>$</c> в слоте 0.</summary>
public sealed record BoundObjectCreationExpression(
	TypeSymbol TypeSymbol,
	FunctionSymbol Constructor,
	IReadOnlyList<BoundExpression> Arguments,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);

/// <summary>Узел-заглушка после ошибки; не порождает каскада диагностик.</summary>
public sealed record BoundErrorExpression(TextSpan Span) : BoundExpression(ErrorType.Instance, Span);
