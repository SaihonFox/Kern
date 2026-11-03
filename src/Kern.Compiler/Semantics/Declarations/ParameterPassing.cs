namespace Kern.Compiler.Semantics.Declarations;

/// <summary>
/// Способ передачи параметра (spec §6):
/// <c>Value</c> — по значению, <c>In</c> (<c>&amp;T</c>) — только чтение,
/// <c>Ref</c> (<c>*T</c>) — изменяемая ссылка на место вызова.
/// </summary>
public enum ParameterPassing
{
	Value,
	In,
	Ref,
}
