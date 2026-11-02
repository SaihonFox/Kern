using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>
/// Общий порядок значений для <c>sorted_dict</c>/<c>sorted_set</c>/<c>priority_queue</c>
/// (spec §16, «Зафиксированный контракт 2026-11-02»): числа сравниваются численно,
/// <c>bool</c>/<c>char</c>/<c>str</c> — естественно, <c>unit</c> — структурно, а
/// <c>type</c> — через объявленный <c>op compare</c>. Неподдерживаемый вид — паника
/// по-русски, потому что «не упорядочивается» лучше показать явно, чем молча
/// разложить по хэшу ссылки.
/// </summary>
internal static class KernValueOrdering
{
	/// <summary>Сравнить два значения; результат -1, 0 или 1.</summary>
	public static int Compare(KValue a, KValue b, Interpreter? interpreter)
	{
		if (a.IsNumber && b.IsNumber)
			return KValueMath.StructuralCompare(a, b);

		if (a.Kind != b.Kind)
			throw new KernPanic($"сравнение: нельзя сравнить {a.TypeName} и {b.TypeName}");

		switch (a.Kind)
		{
			case ValueKind.Bool:
			case ValueKind.Char:
				return System.Math.Sign(a.Bits.CompareTo(b.Bits));
			case ValueKind.Str:
				return System.Math.Sign(string.CompareOrdinal(a.AsStr, b.AsStr));
			case ValueKind.Ref when a.Ref is KObject left && b.Ref is KObject right:
				if (left.IsUnit && right.IsUnit)
					return KValueMath.StructuralCompare(a, b);
				if (interpreter is not null && FindCompare(interpreter, left.Type.Definition, []) is { } function)
					return Apply(interpreter, function, a, b);
				throw new KernPanic(
					$"сравнение не поддерживается для типа '{left.Type.Canonical}': нужен op compare");
			case ValueKind.Ref when a.Ref is KTuple && b.Ref is KTuple:
				return KValueMath.StructuralCompare(a, b);
			default:
				throw new KernPanic($"сравнение не поддерживается для типа '{a.TypeName}'");
		}
	}

	private static int Apply(Interpreter interpreter, FunctionDef function, KValue a, KValue b)
	{
		KValue result = interpreter.RunValue(KValue.Obj(function), a, b);
		if (result.Kind != ValueKind.I32)
			throw new KernPanic($"op compare вернул {result.TypeName}, ожидался i32");
		return System.Math.Sign(result.AsI32);
	}

	/// <summary>
	/// Найти объявленный <c>op compare(T) -&gt; i32</c> у типа или его родителей.
	/// Повторяет поиск компаратора словаря (<c>Interpreter.DictComparer</c>), но для
	/// упорядочивания.
	/// </summary>
	private static FunctionDef? FindCompare(
		Interpreter interpreter,
		TypeDef definition,
		HashSet<TypeDef> visiting)
	{
		if (!visiting.Add(definition))
			return null;

		foreach (int index in definition.Methods)
		{
			FunctionDef function = interpreter.Module.FunctionPool[index];
			if (function.Name.EndsWith("compare", StringComparison.Ordinal) &&
				(function.Flags & MemberFlags.Static) == 0 &&
				function.ParamTypes.Length >= 1)
			{
				return function;
			}
		}

		// Оператор мог не попасть в TypeDef.Methods (например, тип из кэша/линковки):
		// ищем по владельцу в пуле функций.
		int ownerIndex = interpreter.Module.TypePool.IndexOf(definition);
		
		if (ownerIndex >= 0)
		{
			foreach (FunctionDef function in interpreter.Module.FunctionPool)
			{
				if (function.OwnerType == ownerIndex &&
					function.Name.EndsWith("compare", StringComparison.Ordinal) &&
					(function.Flags & MemberFlags.Static) == 0 &&
					function.ParamTypes.Length >= 1)
				{
					return function;
				}
			}
		}

		foreach (TypeSig parent in definition.Parents)
		{
			try
			{
				if (interpreter.Types.FromSig(parent) is KClassType parentType &&
					FindCompare(interpreter, parentType.Definition, visiting) is { } inherited)
				{
					return inherited;
				}
			}
			catch (InvalidOperationException)
			{
				// Повреждённая ссылка на родителя: считаем, что compare не объявлен.
			}
		}

		return null;
	}
}
