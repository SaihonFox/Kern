using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record TypedCollectionExpression(
	TypeSyntax? ElementType,
	TypeSyntax? KeyType,
	TypeSyntax? ValueType,
	IReadOnlyList<ExpressionNode> Items,
	IReadOnlyList<CollectionEntryNode> Entries,
	TextSpan Span) : ExpressionNode(Span)
{
	public bool IsDictionary => KeyType is not null;
}
