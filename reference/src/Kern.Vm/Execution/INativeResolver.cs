using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>Делегат вызова нативного члена: (self, args, typeArgs) → результат. Для async возвращает KValue(KTask).</summary>
public delegate KValue NativeInvoker(Interpreter vm, ReadOnlySpan<KValue> args, KType[] typeArgs);

/// <summary>Мост VM → C#-реализации. Реализован в Kern.Binding.NativeResolver (кэширует скомпилированные делегаты).</summary>
public interface INativeResolver
{
	NativeInvoker Resolve(string nativeKey);
}
