using Kern.Compiler.Lexing;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;

namespace Kern.Compiler.Semantics.Overloads;

/// <summary>
/// Аргумент вызова для разрешения перегрузки. Выражение к этому моменту уже привязано, поэтому для лямбд
/// <see cref="Type"/> — полный <see cref="FuncType"/> (параметры лямбды в Kern всегда типизированы явно;
/// не заданный возвращаемый тип биндер выводит по телу ДО разрешения перегрузки).
/// </summary>
/// <param name="Type">Естественный тип выражения (для числового литерала без суффикса — i32/f64 по правилу биндера).</param>
/// <param name="Span">Диапазон аргумента (для диагностик).</param>
/// <param name="Name">Имя для именованного аргумента `name: value`; иначе null.</param>
/// <param name="Literal">Числовой литерал, если аргумент — он. Литерал без суффикса допускает константное приведение к любому
/// числовому типу, вмещающему значение (см. <see cref="ConstantConversions"/>).</param>
public sealed record CallArgument(TypeSymbol Type, TextSpan Span, string? Name = null, NumberValue? Literal = null);

/// <summary>Запрос на разрешение перегрузки: всё, что известно о месте вызова, кроме самих кандидатов.</summary>
/// <param name="Arguments">Аргументы в порядке исходника (без получателя).</param>
/// <param name="TypeArguments">Явные generic-аргументы `f&lt;A, B&gt;(...)`; пусто — вывод.</param>
/// <param name="Receiver">
/// Тип получателя: объект `obj.m()` (нужен для подстановки generic-аргументов типа-владельца), расширяемое значение
/// для функций-расширений (участвует как нулевой аргумент: приведение и вывод), конструируемый тип для конструкторов.
/// null — вызов свободной функции или статического члена.
/// </param>
/// <param name="OwnerMap">
/// Явная подстановка generic-параметров типа-владельца (нативные generic-типы, где символ не знает своих аргументов).
/// Для пользовательских типов вычисляется автоматически из <paramref name="Receiver"/>.
/// </param>
public sealed record OverloadRequest(
	IReadOnlyList<CallArgument> Arguments,
	IReadOnlyList<TypeSymbol>? TypeArguments = null,
	TypeSymbol? Receiver = null,
	IReadOnlyDictionary<TypeParamSymbol, TypeSymbol>? OwnerMap = null)
{
	public IReadOnlyList<TypeSymbol> ExplicitTypeArguments => TypeArguments ?? [];
	public bool HasErrorArguments => Arguments.Any(a => a.Type.IsError) || (Receiver?.IsError ?? false);
}
