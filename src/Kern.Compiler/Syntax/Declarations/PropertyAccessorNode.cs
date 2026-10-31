using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>Вид accessor'а свойства (spec §4).</summary>
public enum AccessorKind
{
	Get,
	Set,
	Init
}

/// <summary>
/// Один accessor свойства: <c>get</c>, <c>set</c> или <c>init</c>.
/// Тело необязательно: <c>get;</c>, <c>get =&gt; expr</c>, <c>get { ... }</c> или <c>get expr</c>.
/// </summary>
public sealed record PropertyAccessorNode(
	AccessorKind Kind,
	SyntaxNode? Body,
	TextSpan Span) : SyntaxNode(Span)
{
	/// <summary>Параметры accessor'а: допустимы только у <c>set</c>, не более одного.</summary>
	public IReadOnlyList<ParameterNode> Parameters { get; init; } = [];

	/// <summary>Есть ли у accessor'а тело; отсутствие тела делает свойство автосвойством.</summary>
	public bool HasBody => Body is not null;
}
