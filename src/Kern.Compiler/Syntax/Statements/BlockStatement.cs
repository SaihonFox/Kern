using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record BlockStatement(IReadOnlyList<StatementNode> Statements, TextSpan Span) : StatementNode(Span);
