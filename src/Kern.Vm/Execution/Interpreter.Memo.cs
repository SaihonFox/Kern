using Kern.Bytecode;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>
/// <c>#[memo]</c> (spec §7): кэш результатов функции по аргументам. Ключ — функция и
/// структурно равные аргументы; значения-<c>unit</c> копируются, чтобы вызывающий не мог
/// испортить кэш. Кэш живёт в интерпретаторе (у <see cref="Interpreter.Fork"/> свой),
/// поэтому не требует блокировок; асинхронные функции, конструкторы и генераторы не
/// кэшируются.
/// </summary>
public sealed partial class Interpreter
{
	private readonly Dictionary<MemoKey, KValue> _memo = new(new MemoKeyComparer());

	private readonly record struct MemoKey(FunctionDef Function, KValue[] Arguments);

	private sealed class MemoKeyComparer : IEqualityComparer<MemoKey>
	{
		public bool Equals(MemoKey x, MemoKey y)
		{
			if (!ReferenceEquals(x.Function, y.Function) || x.Arguments.Length != y.Arguments.Length)
				return false;
			for (int index = 0; index < x.Arguments.Length; index++)
				if (!x.Arguments[index].Equals(y.Arguments[index]))
					return false;
			return true;
		}

		public int GetHashCode(MemoKey key)
		{
			HashCode hash = new();
			hash.Add(key.Function);
			foreach (KValue value in key.Arguments)
				hash.Add(value);
			return hash.ToHashCode();
		}
	}

	private static bool CanMemoize(FunctionDef function) =>
		(function.Flags & MemberFlags.Memo) != 0 &&
		(function.Flags & (MemberFlags.Async | MemberFlags.Ctor | MemberFlags.Generator | MemberFlags.Native)) == 0;

	/// <summary>Копия значения для кэша: unit/unit! копируются, остальное хранится как есть.</summary>
	private static KValue MemoValue(KValue value) =>
		value.Ref is KObject { Type: KClassType type } instance &&
		type.Definition.Kind is TypeDefKind.Unit or TypeDefKind.ImmutableUnit
			? KValue.Obj(instance.CopyUnit())
			: value;
}
