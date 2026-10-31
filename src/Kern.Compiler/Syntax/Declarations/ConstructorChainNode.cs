using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>
/// Цепочка конструкторов <c>: $(args)</c> (spec §16): вызов другого конструктора того же типа.
/// </summary>
public sealed record ConstructorChainNode(
	IReadOnlyList<ExpressionNode> Arguments,
	TextSpan Span) : SyntaxNode(Span);
