using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Execution;

namespace Kern.Runtime.Tasks;

/// <summary>Члены `task&lt;T&gt;` (доступны после `use kern.tasks`): состояние, ошибка, отмена, блокирующий результат.</summary>
[KernType("task")]
public static class TaskMembers
{
	[KernProperty] public static bool Done<T>(KTaskRef<T> self) => self.Inner.IsCompleted;
	[KernProperty] public static bool Failed<T>(KTaskRef<T> self) => self.Inner.IsFaulted;
	[KernProperty] public static bool Cancelled<T>(KTaskRef<T> self) => self.Inner.IsCanceled;

	/// <summary>Сообщение ошибки упавшей задачи; nil, если не упала.</summary>
	[KernProperty] public static string? Error<T>(KTaskRef<T> self) => self.Inner.Error?.Message;

	/// <summary>Отменить задачу: ожидающие её получат ошибку «задача отменена».</summary>
	[KernMember] public static void Cancel<T>(KTaskRef<T> self) => self.Inner.Cancel();

	/// <summary>Блокирующее ожидание результата (крутит цикл событий); в async-коде используйте `await`.</summary>
	[KernProperty] public static T Result<T>(KTaskRef<T> self, [KernVm] Interpreter vm) => Kv.As<T>(self.Inner.GetResult(vm));
}
