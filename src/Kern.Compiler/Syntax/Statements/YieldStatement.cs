using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary><c>yield value</c> в генераторе (spec §6, раунд 27): выдать один элемент.</summary>
public sealed record YieldStatement(ExpressionNode? Value, TextSpan Span) : StatementNode(Span);
