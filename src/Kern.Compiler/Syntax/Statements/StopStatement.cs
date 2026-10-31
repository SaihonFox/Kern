using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>
/// <c>stop</c> / <c>stop outer</c>: выход из ближайшего или помеченного цикла (spec §3).
/// <see cref="Target"/> = null — ближайший цикл; имя метки разбирается только на той же
/// строке, иначе следующий идентификатор остаётся отдельным оператором.
/// </summary>
public sealed record StopStatement(
	TextSpan Span,
	string? Target = null,
	TextSpan TargetSpan = default) : StatementNode(Span);
