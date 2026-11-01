using Kern.Bytecode;
using Kern.Vm.Types;

namespace Kern.Vm.Execution;

public sealed partial class Interpreter
{
	/// <summary>
	/// Q23: если у типа ключа есть <c>op hash</c>/<c>op ==</c>, словарь получает компаратор,
	/// который вызывает их в VM. Иначе остаётся стандартное сравнение <see cref="Values.KValue"/>.
	/// </summary>
	private IEqualityComparer<Values.KValue>? DictionaryKeyComparer(KType keyType)
	{
		if (keyType is not KClassType { Definition: { } definition })
			return null;

		FunctionDef? hash = FindOperator(definition, "hash", 1, []);
		FunctionDef? equals = FindOperator(definition, "==", 2, []);
		return hash is null && equals is null
			? null
			: new Values.KernDictComparer(this, hash, equals);
	}

	private FunctionDef? FindOperator(
		TypeDef definition,
		string name,
		int parameterCount,
		HashSet<TypeDef> visiting)
	{
		if (!visiting.Add(definition))
			return null;

		foreach (int index in definition.Methods)
		{
			FunctionDef function = Module.FunctionPool[index];
			if (function.Name == name &&
				(function.Flags & MemberFlags.Static) == 0 &&
				function.ParamTypes.Length == parameterCount)
			{
				return function;
			}
		}

		foreach (TypeSig parent in definition.Parents)
		{
			try
			{
				if (Types.FromSig(parent) is KClassType parentType &&
					FindOperator(parentType.Definition, name, parameterCount, visiting) is { } inherited)
				{
					return inherited;
				}
			}
			catch (InvalidOperationException)
			{
				// Повреждённая ссылка на родителя: оставляем стандартное сравнение.
			}
		}

		return null;
	}
}
