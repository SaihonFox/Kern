using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record LoopStatement(
	BlockStatement Body,
	TextSpan Span,
	LoopExpression? Source = null,
	LoopLabel? Label = null) : StatementNode(Span);
