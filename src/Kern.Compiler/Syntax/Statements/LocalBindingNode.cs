using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record LocalBindingNode(string Name, ExpressionNode? Initializer, TextSpan Span) : SyntaxNode(Span);
