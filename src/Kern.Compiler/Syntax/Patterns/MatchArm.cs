using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record MatchArm(PatternNode Pattern, ExpressionNode Body, TextSpan Span, ExpressionNode? Guard = null) : SyntaxNode(Span);
