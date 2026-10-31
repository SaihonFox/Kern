namespace Kern.Compiler.Syntax;

/// <summary>
/// Модификатор доступа объявления (spec §2, §16): <c>pub</c>, <c>priv</c>, <c>prot</c>, <c>int</c>.
/// </summary>
public enum Accessibility
{
	Public,
	Private,
	Protected,
	Internal
}
