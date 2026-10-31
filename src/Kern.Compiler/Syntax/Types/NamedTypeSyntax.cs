using System.Text;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>Именованный тип: <c>i32</c>, <c>list&lt;T&gt;</c>, <c>T?</c>, <c>i32!?</c>.</summary>
public sealed record NamedTypeSyntax(
	string Name,
	IReadOnlyList<TypeSyntax> Arguments,
	bool IsNullable,
	bool IsSingleAssignment,
	TextSpan Span) : TypeSyntax(Span)
{
	/// <summary>Простое имя без аргументов и маркеров (для восстановления после ошибки).</summary>
	public static NamedTypeSyntax Plain(string name, TextSpan span) => new(name, [], false, false, span);

	public override string ToString()
	{
		var builder = new StringBuilder(Name);
		if (Arguments.Count > 0)
			builder.Append('<').AppendJoin(',', Arguments).Append('>');
		if (IsSingleAssignment)
			builder.Append('!');
		if (IsNullable)
			builder.Append('?');
		return builder.ToString();
	}
}
