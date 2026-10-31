using System.Text;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>Функциональный тип: <c>fun(i32, str) -&gt; bool</c>, <c>fun -&gt; void</c>.</summary>
public sealed record FunctionTypeSyntax(
	IReadOnlyList<TypeSyntax> Parameters,
	TypeSyntax? ReturnType,
	TextSpan Span) : TypeSyntax(Span)
{
	public override string ToString()
	{
		var builder = new StringBuilder("fun(");
		builder.AppendJoin(',', Parameters);
		builder.Append(')');
		if (ReturnType is not null)
			builder.Append("->").Append(ReturnType);
		return builder.ToString();
	}
}
