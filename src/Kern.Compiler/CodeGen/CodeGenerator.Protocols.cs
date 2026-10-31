using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;

namespace Kern.Compiler.CodeGen;

/// <summary>
/// B6b1/B8a/B8c: генерация протоколов <c>hash</c>/<c>compare</c>/<c>clone</c> (spec §9).
/// Работу целиком делает VM: <c>ValueHash</c>/<c>ValueHashFields</c> снимают значение и кладут
/// <c>u64</c>, <c>ValueCompare</c>/<c>ValueCompareFields</c> снимают правый и левый операнды и
/// кладут <c>i32</c> (-1/0/1), <c>CloneObject</c> заменяет объект неглубокой копией.
/// Варианты <c>*Fields</c> — производные (B8a): объект любого вида хэшируется/сравнивается по
/// reified-типу и полям. Опкоды добавлены в конец набора (B8a/B8c), формат <c>.kbc</c> не менялся.
/// </summary>
public sealed partial class CodeGenerator
{
	private void EmitHash(BoundHashExpression hash)
	{
		EmitExpression(hash.Target);
		Emit(hash.Structural ? OpCode.ValueHashFields : OpCode.ValueHash);
	}

	private void EmitCompare(BoundCompareExpression compare)
	{
		EmitExpression(compare.Left);
		EmitExpression(compare.Right);
		Emit(compare.Structural ? OpCode.ValueCompareFields : OpCode.ValueCompare);
	}

	private void EmitClone(BoundCloneExpression clone)
	{
		EmitExpression(clone.Target);
		Emit(OpCode.CloneObject);
	}
}
