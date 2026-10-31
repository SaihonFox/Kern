using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public abstract record DeclarationNode(TextSpan Span) : SyntaxNode(Span);
