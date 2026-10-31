namespace Kern.Compiler.Semantics.Types;

/// <summary>Вид разрешённого типа: по нему Binder и CodeGen выбирают ветку обработки.</summary>
public enum TypeKind
{
	Primitive,
	Class,
	Unit,
	BuiltinGeneric,
	Nullable,
	Union,
	Tuple,
	Function,
	TypeParameter,
	Error,
	Nil
}
