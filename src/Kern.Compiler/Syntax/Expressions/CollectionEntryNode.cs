using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record CollectionEntryNode(
	ExpressionNode Key,
	ExpressionNode Value,
	TextSpan Span) : SyntaxNode(Span);
