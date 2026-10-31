using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record AttributeNode(string Name, IReadOnlyList<string> Arguments, TextSpan Span) : SyntaxNode(Span);
