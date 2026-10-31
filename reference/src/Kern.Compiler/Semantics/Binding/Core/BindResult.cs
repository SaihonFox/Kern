using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;

namespace Kern.Compiler.Semantics.Binding.Core;

/// <summary>
/// Результат привязки выражения-«цели»: значение, тип (для статического доступа и конструкторов),
/// модуль (`io.File`) или группа функций (ещё не вызванных). Используется при разборе `a.b(c)`.
/// </summary>
public abstract record BindResult(TextSpan Span);

public sealed record ValueResult(BoundExpr Value) : BindResult(Value.Span);

public sealed record TypeResult(TypeSymbol Type, TextSpan Span, bool HasExplicitArgs) : BindResult(Span);

public sealed record ModuleResult(string Path, ModuleSymbol? Module, Kern.Binding.Catalog.NativeModule? Native, TextSpan Span) : BindResult(Span);

/// <summary>
/// Группа перегрузок. <see cref="Receiver"/> — значение-получатель (метод экземпляра/расширение); null — свободная или статическая функция.
/// <see cref="OwnerType"/> — тип-владелец для статических функций/конструкторов (нужен для подстановки generic-аргументов).
/// </summary>
public sealed record GroupResult(
	string Name, IReadOnlyList<FunctionSymbol> Candidates, BoundExpr? Receiver, bool IsSafe, TypeSymbol? OwnerType,
	IReadOnlyDictionary<TypeParamSymbol, TypeSymbol>? OwnerMap, TextSpan Span, bool IsConstructorGroup = false) : BindResult(Span);
