using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>Элемент типа-кортежа: тип и необязательное имя поля.</summary>
public sealed record TupleTypeElementSyntax(TypeSyntax Type, string? Name, TextSpan Span) : SyntaxNode(Span)
{
	public override string ToString() => Name is null ? Type.ToString() : $"{Type} {Name}";
}

/// <summary>Тип-кортеж: <c>(i32, str)</c>, <c>(i32 x, f64 y)</c>.</summary>
public sealed record TupleTypeSyntax(
	IReadOnlyList<TupleTypeElementSyntax> Elements,
	TextSpan Span) : TypeSyntax(Span)
{
	public override string ToString() => $"({string.Join(',', Elements)})";
}
