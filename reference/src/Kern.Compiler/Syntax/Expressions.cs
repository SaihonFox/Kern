using Kern.Compiler.Lexing;
using Kern.Core;

namespace Kern.Compiler.Syntax;

// Правило для парсера: generic-вызов vs сравнение.
// После Ident / MemberExpr токен `<` пробуется как список type-аргументов (спекулятивно):
// `<` Type {`,` Type} `>` и СРАЗУ за `>` идёт `(` (вызов) либо `[` для литерала коллекции
// (`array<i32>[..]`). Если разбор типа не удался или следующий токен иной — это операция `<`.

/// <summary>Выражение. В Kern всё — выражение (в т.ч. объявления переменных, if, match, loop).</summary>
public abstract record Expr(TextSpan Span) : Node(Span);

/// <summary>Синтаксическая ошибка (для восстановления парсера).</summary>
public sealed record ErrorExpr(string Message, TextSpan Span) : Expr(Span);

// ---------- Литералы ----------

/// <summary>Число; Value.Suffix — точный тип (`u32`, `f64`, `u8` для `5b`).</summary>
public sealed record NumberLitExpr(NumberValue Value, TextSpan Span) : Expr(Span);
public sealed record StringLitExpr(string Value, TextSpan Span) : Expr(Span);
public sealed record CharLitExpr(int CodePoint, TextSpan Span) : Expr(Span);
public sealed record BoolLitExpr(bool Value, TextSpan Span) : Expr(Span);
public sealed record NilLitExpr(TextSpan Span) : Expr(Span);

/// <summary>Часть интерполированной строки.</summary>
public abstract record InterpPart(TextSpan Span) : Node(Span);
public sealed record InterpText(string Text, TextSpan Span) : InterpPart(Span);

/// <summary>`${expr}`; Debug — `${expr=}`; Format — `${v:.2f}` (без двоеточия); SourceText — текст выражения для debug-вывода.</summary>
public sealed record InterpExpr(Expr Value, bool Debug, string? Format, string SourceText, TextSpan Span) : InterpPart(Span);

public sealed record InterpolatedStringExpr(IReadOnlyList<InterpPart> Parts, TextSpan Span) : Expr(Span);

// ---------- Имена и ссылка на объект ----------

public sealed record IdentExpr(string Name, TextSpan Span) : Expr(Span);

/// <summary>Голый `$` — текущий объект (или левый операнд в операторных расширениях).</summary>
public sealed record SelfExpr(TextSpan Span) : Expr(Span);

/// <summary>`$field` — член текущего объекта.</summary>
public sealed record SelfMemberExpr(string Name, TextSpan Span) : Expr(Span);

/// <summary>`$[A].member` — член конкретного родителя (вызов — CallExpr с этим callee).</summary>
public sealed record ParentMemberExpr(string Parent, string Member, TextSpan Span) : Expr(Span);

// ---------- Операторы ----------

public enum UnaryOp { Plus, Negate, Not }

public sealed record UnaryExpr(UnaryOp Op, Expr Operand, TextSpan Span) : Expr(Span);

public enum BinaryOp
{
	Add, Sub, Mul, Div, Rem, Pow,
	Eq, Ne, RefEq, RefNe, Lt, Gt, Le, Ge,
	And, Or,
	In, NotIn,
	Elvis, // ?:
}

public sealed record BinaryExpr(BinaryOp Op, Expr Left, Expr Right, TextSpan Span) : Expr(Span);

/// <summary>`x is T` / `x !is T` (справа — тип, поэтому отдельно от BinaryExpr).</summary>
public sealed record TypeTestExpr(Expr Operand, TypeRef Type, bool Negated, TextSpan Span) : Expr(Span);

public enum AssignOp { Assign, Add, Sub, Mul, Div, Rem, Pow }

/// <summary>Присваивание (в т.ч. составное). Target: IdentExpr, MemberExpr (в т.ч. `obj?.p`), IndexExpr, SelfMemberExpr...</summary>
public sealed record AssignExpr(AssignOp Op, Expr Target, Expr Value, TextSpan Span) : Expr(Span);

// ---------- Вызовы и доступ ----------

/// <summary>Аргумент вызова/кортежа; Name != null для именованного `name: value`.</summary>
public sealed record Argument(string? Name, Expr Value, TextSpan Span) : Node(Span);

/// <summary>
/// Вызов `f<T>(a, name: b) trailing`. TrailingLambda — лямбда, вынесенная за скобки.
/// Callee может быть MemberExpr (в т.ч. IsSafe) — тогда это вызов метода.
/// </summary>
public sealed record CallExpr(
	Expr Callee, IReadOnlyList<TypeRef> TypeArgs, IReadOnlyList<Argument> Args, LambdaExpr? TrailingLambda, TextSpan Span) : Expr(Span);

/// <summary>
/// `target.name` / `target?.name`. Без вызова `obj.method` — ссылка на метод (различает биндер).
/// </summary>
public sealed record MemberExpr(Expr Target, string Name, bool IsSafe, TextSpan Span) : Expr(Span);

public sealed record IndexExpr(Expr Target, IReadOnlyList<Expr> Indices, TextSpan Span) : Expr(Span);

/// <summary>`typeof<T>()`</summary>
public sealed record TypeofTypeExpr(TypeRef Type, TextSpan Span) : Expr(Span);

/// <summary>`typeof(expr)`</summary>
public sealed record TypeofValueExpr(Expr Operand, TextSpan Span) : Expr(Span);

public sealed record AwaitExpr(Expr Operand, TextSpan Span) : Expr(Span);

// ---------- Управление потоком ----------

/// <summary>`if c a else b`; ветки — блок или одно выражение. Else == null, если не указан.</summary>
public sealed record IfExpr(Expr Condition, Expr Then, Expr? Else, TextSpan Span) : Expr(Span);

/// <summary>`c ? a : b`</summary>
public sealed record TernaryExpr(Expr Condition, Expr Then, Expr Else, TextSpan Span) : Expr(Span);

/// <summary>`match target { ... }`; Target == null — чистые guards `match { x > 0 -> ... }`.</summary>
public sealed record MatchExpr(Expr? Target, IReadOnlyList<MatchArm> Arms, TextSpan Span) : Expr(Span);

/// <summary>
/// `loop var in iterable body`. Variable: BindingPattern / TypePattern (`i32 i`) / WildcardPattern / DestructurePattern.
/// Iterable — RangeExpr (со Step) или коллекция.
/// </summary>
public sealed record LoopExpr(Pattern Variable, Expr Iterable, Expr Body, TextSpan Span, bool IsAsync = false) : Expr(Span);

/// <summary>
/// `loop cond body` (пока условие истинно) и `loop { … }` (бесконечный; Condition == null, выход через `stop`/`ret`).
/// Результат — список значений тела, как у `loop x in …`.
/// `loop { … }: cond` — do-while (PostCondition): тело выполняется минимум раз, `next` переходит к проверке условия.
/// </summary>
public sealed record WhileExpr(Expr? Condition, Expr Body, TextSpan Span, bool PostCondition = false) : Expr(Span);

public sealed record StopExpr(TextSpan Span) : Expr(Span);
public sealed record NextExpr(TextSpan Span) : Expr(Span);

/// <summary>
/// `ret [expr]` — выход из ближайшей функции/лямбды (return). Value == null — `ret` без значения (void).
/// Выражение типа Never: допустимо как ветка if/match/тернарного оператора (`x = if c 5 else ret`).
/// </summary>
public sealed record ReturnExpr(Expr? Value, TextSpan Span) : Expr(Span);

/// <summary>`a..b`, `a..=b`, `a..b:step`; границы могут отсутствовать.</summary>
public sealed record RangeExpr(Expr? Start, Expr? End, bool Inclusive, Expr? Step, TextSpan Span) : Expr(Span);

/// <summary>`{ e1 e2 ... }` — значение последнего выражения.</summary>
public sealed record BlockExpr(IReadOnlyList<Expr> Exprs, TextSpan Span) : Expr(Span);

// ---------- Лямбды ----------

/// <summary>`fun(str s) -> R body` / `fun -> R body`.</summary>
public sealed record LambdaExpr(bool IsAsync, IReadOnlyList<Param> Params, TypeRef? ReturnType, Expr Body, TextSpan Span) : Expr(Span);

// ---------- Переменные ----------

/// <summary>
/// Объявление локальной переменной: `T x = e`, `var x = e`, `T x`, `T x(args)`, `T! x`.
/// Type == null — `var`. CtorArgs != null — конструктор-объявление `T x(args)`.
/// SingleAssign — `!` после типа (`i32!`, `i32!?`, `i32?!`) или `var!`.
/// </summary>
public sealed record VarDeclExpr(
	TypeRef? Type, string Name, TextSpan NameSpan, Expr? Init, IReadOnlyList<Argument>? CtorArgs,
	bool SingleAssign, TextSpan Span, bool IsUse = false) : Expr(Span);

/// <summary>`(i32 a, _, list<i32> ..rest) = e` и `Point(x, y) = e`.</summary>
public sealed record DestructureExpr(DestructurePattern Target, Expr Value, TextSpan Span) : Expr(Span);

// ---------- Коллекции и кортежи ----------

/// <summary>`(1, "a")` или именованный `(x: 1, y: 2)`.</summary>
public sealed record TupleExpr(IReadOnlyList<Argument> Elements, TextSpan Span) : Expr(Span);

/// <summary>`[1, 2]` (ElementType == null) или `<i32>[1, 2]`.</summary>
public sealed record ListExpr(TypeRef? ElementType, IReadOnlyList<Expr> Elements, TextSpan Span) : Expr(Span);

public sealed record DictEntry(Expr Key, Expr Value, TextSpan Span) : Node(Span);

/// <summary>`["k": 1]` (типы null) или `<str: i32>["k": 1]`.</summary>
public sealed record DictExpr(TypeRef? KeyType, TypeRef? ValueType, IReadOnlyList<DictEntry> Entries, TextSpan Span) : Expr(Span);

/// <summary>`array<i32>[1, 2, 3]`.</summary>
public sealed record ArrayExpr(TypeRef? ElementType, IReadOnlyList<Expr> Elements, TextSpan Span) : Expr(Span);
