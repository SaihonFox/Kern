using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Bound;

/// <summary>
/// B6b1/B8a: хэш значения — <c>x.hash()</c> без объявленного <c>op hash</c> (spec §9). Для
/// <c>unit</c>/<c>unit!</c> с хэшируемыми полями, для <c>type</c> — хэш по ссылке, а с
/// <c>#[derive(hash)]</c> — структурный хэш по полям (<see cref="Structural"/>). Codegen
/// печатает получателя и opcode <c>ValueHash</c> или <c>ValueHashFields</c>; тип результата — <c>u64</c>.
/// </summary>
public sealed record BoundHashExpression(
	BoundExpression Target,
	KernType Type,
	TextSpan Span,
	bool Structural = false) : BoundExpression(Type, Span);

/// <summary>
/// B6b1/B8a: структурное сравнение <c>x.compare(y)</c> без объявленного <c>op compare</c> и
/// упорядочивание <c>&lt;</c>/<c>&lt;=</c>/<c>&gt;</c>/<c>&gt;=</c> для сравнимых unit'ов и
/// для <c>type</c> с <c>#[derive(compare)]</c> (<see cref="Structural"/>). Codegen печатает
/// левый операнд, правый и opcode <c>ValueCompare</c> или <c>ValueCompareFields</c>; тип
/// результата — <c>i32</c> (-1/0/1), поэтому упорядочивание сравнивает его с нулевым литералом.
/// </summary>
public sealed record BoundCompareExpression(
	BoundExpression Left,
	BoundExpression Right,
	KernType Type,
	TextSpan Span,
	bool Structural = false) : BoundExpression(Type, Span);

/// <summary>
/// B8c: <c>x.clone()</c> у <c>type</c> с <c>#[derive(clone)]</c> — неглубокая копия
/// объекта: поля-значения (<c>unit</c>/<c>unit!</c>) копируются, ссылки (<c>type</c>,
/// коллекции) остаются общими. Codegen печатает получателя и opcode <c>CloneObject</c>;
/// тип результата — тип получателя.
/// </summary>
public sealed record BoundCloneExpression(
	BoundExpression Target,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);
