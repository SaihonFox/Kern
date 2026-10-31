using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record ReturnStatement(ExpressionNode? Expression, TextSpan Span) : StatementNode(Span);
