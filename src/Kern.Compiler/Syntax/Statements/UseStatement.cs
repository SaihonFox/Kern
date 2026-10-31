using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record UseStatement(
	TypeSyntax? Type,
	string Name,
	ExpressionNode Initializer,
	bool IsInferred,
	TextSpan Span) : StatementNode(Span);
