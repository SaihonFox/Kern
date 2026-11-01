using Kern.Binding.Values;
using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Http;

/// <summary>Мелкие помощники для нативных привязок backend.</summary>
internal static class KernTypeHelpers
{
	/// <summary>Kern-значение <c>list&lt;str&gt;</c>.</summary>
	public static KValue StringList(IEnumerable<string> items) =>
		KValue.Obj(new KList(null, [.. items.Select(KValue.Str)]));

	/// <summary>Положить CLR-объект в Kern-значение как нативный handle.</summary>
	public static KValue Box(object value) => KValue.Obj(new KNativeBox(value));

	public static bool TryUnbox<T>(KValue value, out T result)
		where T : class
	{
		if (value.Ref is KNativeBox box && box.Value is T typed)
		{
			result = typed;
			return true;
		}

		if (value.Ref is T direct)
		{
			result = direct;
			return true;
		}

		result = null!;
		return false;
	}

	/// <summary>Reified-тип нативного handle-типа из пула модуля (claims, socket, …).</summary>
	public static KType NativeType(Interpreter interpreter, string name)
	{
		foreach (TypeDef definition in interpreter.Module.TypePool)
			if (definition.Name == name && definition.GenericParams.Count == 0)
				return interpreter.Types.Named(definition);
		return interpreter.Types.Prim(PrimKind.Object);
	}
}
