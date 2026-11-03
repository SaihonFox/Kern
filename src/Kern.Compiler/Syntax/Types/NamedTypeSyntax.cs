using System.Text;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>Именованный тип: <c>i32</c>, <c>list&lt;T&gt;</c>, <c>T?</c>, <c>i32!?</c>.</summary>
/// <param name="ArgumentLabels">
/// Метки generic-аргументов, параллельны <paramref name="Arguments"/>: <c>null</c> — метки нет
/// (<c>result&lt;T[ok], E[error]&gt;</c> даёт <c>["ok", "error"]</c>). Семантику меток проверяет
/// <c>TypeResolver</c> (spec §16).
/// </param>
public sealed record NamedTypeSyntax(
	string Name,
	IReadOnlyList<TypeSyntax> Arguments,
	IReadOnlyList<string?> ArgumentLabels,
	bool IsNullable,
	bool IsSingleAssignment,
	TextSpan Span,
	bool IsIn = false,
	bool IsRef = false) : TypeSyntax(Span)
{
	/// <summary>Простое имя без аргументов, меток и маркеров (для восстановления после ошибки).</summary>
	public static NamedTypeSyntax Plain(string name, TextSpan span) => new(name, [], [], false, false, span);

	/// <summary>Есть ли хотя бы одна метка generic-аргумента.</summary>
	public bool HasArgumentLabels => ArgumentLabels.Any(label => label is not null);

	public override string ToString()
	{
		var builder = new StringBuilder();
		if (IsIn)
			builder.Append('&');
		else if (IsRef)
			builder.Append('*');
		builder.Append(Name);
		if (Arguments.Count > 0)
		{
			builder.Append('<');
			for (var index = 0; index < Arguments.Count; index++)
			{
				if (index > 0)
					builder.Append(',');
				builder.Append(Arguments[index]);
				if (index < ArgumentLabels.Count && ArgumentLabels[index] is { } label)
					builder.Append('[').Append(label).Append(']');
			}
			builder.Append('>');
		}
		if (IsSingleAssignment)
			builder.Append('!');
		if (IsNullable)
			builder.Append('?');
		return builder.ToString();
	}
}
