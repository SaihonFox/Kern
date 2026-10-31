using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record IdentifierExpression(string Name, TextSpan Span) : ExpressionNode(Span);
