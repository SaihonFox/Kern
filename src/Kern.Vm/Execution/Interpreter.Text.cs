using Kern.Bytecode;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>
/// Контекстные конверсии <c>str</c> ↔ <c>list&lt;char&gt;</c> (spec §3): одна и та же
/// операция, что и <c>text.chars</c>/<c>text.from_chars</c>, но без нативного вызова.
/// </summary>
public sealed partial class Interpreter
{
	/// <summary>Снять строку и положить <c>list&lt;char&gt;</c> из её UTF-16 единиц.</summary>
	private void StrChars(ref OperandStack stack)
	{
		KValue value = stack.Pop();
		if (value.Kind != ValueKind.Str)
			throw new KernPanic($"str -> list<char>: ожидалась строка, получено {value.TypeName}");
		var items = new List<KValue>(value.AsStr.Length);
		foreach (char character in value.AsStr)
			items.Add(KValue.Char(character));
		stack.Push(KValue.Obj(new KList(_types.Prim(PrimKind.Char), items)));
	}

	/// <summary>Снять <c>list&lt;char&gt;</c> и положить строку.</summary>
	private void CharsToStr(ref OperandStack stack)
	{
		KValue value = stack.Pop();
		if (value.Ref is not KList list)
			throw new KernPanic($"list<char> -> str: ожидался список, получено {value.TypeName}");
		var builder = new System.Text.StringBuilder(list.Count);
		foreach (KValue item in list.Items)
		{
			if (item.Kind != ValueKind.Char)
				throw new KernPanic($"list<char> -> str: ожидался char, получено {item.TypeName}");
			builder.Append(item.AsChar);
		}
		stack.Push(KValue.Str(builder.ToString()));
	}
}
