using Kern.Bytecode;
using Kern.Vm.Execution;

namespace Kern.Vm.Values;

/// <summary>
/// Q23: компаратор ключей словаря, который вызывает объявленные <c>op hash</c>/<c>op ==</c>
/// пользовательского типа. Без override'ов словарь использует обычные
/// <see cref="KValue.Equals(object)"/>/<see cref="KValue.GetHashCode"/>.
/// </summary>
internal sealed class KernDictComparer(
	Interpreter interpreter,
	FunctionDef? hash,
	FunctionDef? equals) : IEqualityComparer<KValue>
{
	public bool Equals(KValue x, KValue y)
	{
		if (equals is not null && x.Ref is KObject left && y.Ref is KObject right &&
			ReferenceEquals(left.Type.Definition, right.Type.Definition))
		{
			return interpreter.RunValue(KValue.Obj(equals), x, y).AsBool;
		}

		return KValueMath.AreEqual(x, y);
	}

	public int GetHashCode(KValue value)
	{
		if (hash is not null && value.Ref is KObject)
			return unchecked((int)interpreter.RunValue(KValue.Obj(hash), value).AsU64);
		// Своё равенство без своего хэша: все ключи в одном ведре, но контракт не нарушен.
		return equals is not null ? 0 : value.GetHashCode();
	}
}
