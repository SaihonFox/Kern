using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>Члены result&lt;T, E&gt;: ошибки как значения (в языке нет try/catch).</summary>
[KernType("result")]
public static class ResultApi
{
	[KernProperty] public static bool IsOk<T, E>(KResult<T, E> self) => self.Inner.IsOk;
	[KernProperty] public static bool IsErr<T, E>(KResult<T, E> self) => !self.Inner.IsOk;

	/// <summary>Значение Ok; для Err — panic с описанием ошибки.</summary>
	[KernMember] public static T Unwrap<T, E>(KResult<T, E> self) =>
		self.Inner.IsOk ? Kv.As<T>(self.Inner.Value) : throw new KernPanic("unwrap() для Err: " + ValueOps.ToStr(self.Inner.Value));

	[KernMember] public static E UnwrapErr<T, E>(KResult<T, E> self) =>
		!self.Inner.IsOk ? Kv.As<E>(self.Inner.Value) : throw new KernPanic("unwrap_err() для Ok: " + ValueOps.ToStr(self.Inner.Value));

	[KernMember] public static T UnwrapOr<T, E>(KResult<T, E> self, T fallback) => self.Inner.IsOk ? Kv.As<T>(self.Inner.Value) : fallback;

	[KernMember] public static T? OkOrNil<T, E>(KResult<T, E> self) => Kv.As<T>(self.Inner.IsOk ? self.Inner.Value : KValue.Nil);
	[KernMember] public static E? ErrOrNil<T, E>(KResult<T, E> self) => Kv.As<E>(self.Inner.IsOk ? KValue.Nil : self.Inner.Value);

	[KernMember] public static KResult<U, E> Map<T, E, U>(KResult<T, E> self, KFunc<T, U> f) =>
		self.Inner.IsOk ? KResult<U, E>.Ok(f.Invoke(self.Inner.Value)) : KResult<U, E>.Err(self.Inner.Value);

	[KernMember] public static KResult<T, F> MapErr<T, E, F>(KResult<T, E> self, KFunc<E, F> f) =>
		self.Inner.IsOk ? KResult<T, F>.Ok(self.Inner.Value) : KResult<T, F>.Err(f.Invoke(self.Inner.Value));

	/// <summary>Цепочка: f возвращает result; ошибка проходит без изменений.</summary>
	[KernMember] public static KResult<U, E> AndThen<T, E, U>(KResult<T, E> self, KFunc<T, KResult<U, E>> f) =>
		self.Inner.IsOk ? new KResult<U, E>((KResultObject)f.Invoke(self.Inner.Value).Ref!) : KResult<U, E>.Err(self.Inner.Value);

	[KernMember] public static T UnwrapOrElse<T, E>(KResult<T, E> self, KFunc<E, T> f) =>
		self.Inner.IsOk ? Kv.As<T>(self.Inner.Value) : Kv.As<T>(f.Invoke(self.Inner.Value));
}
