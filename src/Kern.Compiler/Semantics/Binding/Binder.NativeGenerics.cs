using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>Подстановка аргументов нативного generic-типа в тип результата члена.</summary>
public sealed partial class Binder
{
	/// <summary>
	/// Для <c>deque&lt;i32&gt;.pop_front() -&gt; i32</c>: у нового нативного generic-типа
	/// (<c>deque</c>, <c>lazy</c>, <c>span</c>, <c>ring_buffer</c>) сигнатура объявлена через
	/// <c>T</c>; подставляем аргументы получателя. Встроенные типы обрабатывает
	/// <see cref="BuiltinMemberSignatures"/>.
	/// </summary>
	private static KernType SubstituteNativeReturn(KernType type, KernType receiver)
	{
		if (receiver is not ClassType { Symbol.Kind: Kern.Compiler.Semantics.Types.SymbolKind.Native } native ||
			native.Arguments.Count == 0)
			return type;

		var substitution = new Dictionary<string, KernType>(StringComparer.Ordinal);
		IReadOnlyList<string> names = native.Symbol.GenericParameterNames;
		for (var index = 0; index < names.Count && index < native.Arguments.Count; index++)
			substitution[names[index]] = native.Arguments[index];
		return TypeSubstitution.Apply(type, substitution);
	}
}
