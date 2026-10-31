using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record LambdaExpression(
	IReadOnlyList<ParameterNode> Parameters,
	TypeSyntax? ReturnType,
	SyntaxNode Body,
	TextSpan Span) : ExpressionNode(Span);
