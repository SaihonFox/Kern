using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>
/// Структурный паттерн unit-типа <c>Point { x: 0, y: var y }</c> / <c>Point(x: 0, ..)</c>.
/// <see cref="RestCount"/> хранит число встреченных <c>..</c>, чтобы binder мог сообщить
/// KRN2113 о повторном rest'е; <see cref="RestName"/> — имя <c>..name</c> (пока KRN2114).
/// </summary>
public sealed record StructuralPattern(
	TypeSyntax Type,
	IReadOnlyList<NamedFieldPattern> Fields,
	bool HasRest,
	TextSpan Span,
	string? RestName = null,
	int RestCount = 0) : PatternNode(Span);
