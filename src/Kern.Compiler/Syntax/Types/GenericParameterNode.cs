using System.Text;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>Generic-параметр объявления: <c>T</c>, <c>K[IHashable]</c>, <c>V[ctor()]</c>.</summary>
public sealed record GenericParameterNode(
	string Name,
	IReadOnlyList<TypeSyntax> Constraints,
	bool HasCtorConstraint,
	TextSpan Span) : SyntaxNode(Span)
{
	public override string ToString()
	{
		if (Constraints.Count == 0 && !HasCtorConstraint)
			return Name;
		var builder = new StringBuilder(Name);
		builder.Append('[');
		if (Constraints.Count > 0)
			builder.AppendJoin(',', Constraints);
		if (HasCtorConstraint)
		{
			if (Constraints.Count > 0)
				builder.Append(',');
			builder.Append("ctor()");
		}
		builder.Append(']');
		return builder.ToString();
	}
}
