using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>
/// Выбор родителя в выражении: <c>$[A].member</c> (spec §2). Член после точки разбирается
/// обычным постфиксным разбором, поэтому вызовы, поля и свойства работают единообразно.
/// </summary>
public sealed record SuperExpression(string ParentName, TextSpan Span) : ExpressionNode(Span);
