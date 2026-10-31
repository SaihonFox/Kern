using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>
/// Запись типа в исходном коде. Хранит только синтаксис (имена, аргументы, маркеры),
/// разрешение в <c>KernType</c> выполняет семантический слой.
/// </summary>
public abstract record TypeSyntax(TextSpan Span)
{
	/// <summary>Канонический текст: без пробелов, маркеры в порядке <c>!</c> затем <c>?</c>.</summary>
	public abstract override string ToString();
}
