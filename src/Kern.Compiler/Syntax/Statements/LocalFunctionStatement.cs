using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record LocalFunctionStatement(FunctionDeclaration Function, TextSpan Span) : StatementNode(Span);
