namespace Kern.Compiler.Semantics.Declarations;

/// <summary>Вид функции-члена. Accessor'ы свойства — отдельные функции (spec §4).</summary>
public enum FunctionKind
{
	Function,
	Method,
	Constructor,
	Operator,
	Getter,
	Setter,
	Init,
	ParentSelection
}
