using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>
/// <c>next</c> / <c>next outer</c>: переход к следующей итерации ближайшего или помеченного
/// цикла (spec §3). <see cref="Target"/> = null — ближайший цикл.
/// </summary>
public sealed record NextStatement(
	TextSpan Span,
	string? Target = null,
	TextSpan TargetSpan = default) : StatementNode(Span);
