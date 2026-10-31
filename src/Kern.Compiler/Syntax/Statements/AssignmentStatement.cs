using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record AssignmentStatement(
	ExpressionNode Target,
	string Operator,
	ExpressionNode Value,
	TextSpan Span) : StatementNode(Span);
