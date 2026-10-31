using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record DictionaryExpression(
	IReadOnlyList<CollectionEntryNode> Entries,
	TextSpan Span) : ExpressionNode(Span);
