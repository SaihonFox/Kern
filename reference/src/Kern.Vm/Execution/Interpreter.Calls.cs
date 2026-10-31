using Kern.Bytecode;
using Kern.Vm.Async;
using Kern.Vm.Reflection;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

public sealed partial class Interpreter
{
	private readonly Dictionary<string, NativeInvoker> _nativeCache = [];

	internal NativeInvoker GetNative(string key)
	{
		if (!_nativeCache.TryGetValue(key, out var inv))
			_nativeCache[key] = inv = Natives.Resolve(key);
		return inv;
	}

	/// <summary>Создать фрейм: аргументы копируются из src[start..start+argc].</summary>
	internal Frame NewFrame(FunctionDef fd, int fi, KValue[] src, int start, int argc, KValue self, bool hasSelf,
		KCell[] caps, KType[] classArgs, KType[] methodArgs, Frame? caller, bool returnsThis = false)
	{
		if (fd.Code.Length == 0) throw new KernPanic($"вызов метода без реализации: {FrameName(fd)}");
		int depth = caller is null ? 0 : caller.Depth + 1;
		if (depth > MaxFrameDepth) throw new KernPanic("переполнение стека вызовов");
		int argSlots = Math.Max(argc, fd.Params.Count);
		int stackBase = argSlots + fd.LocalCount;
		var regs = new KValue[stackBase + Math.Max(fd.MaxStack, 4) + 4];
		if (argc > 0) Array.Copy(src, start, regs, 0, argc);
		return new Frame
		{
			Def = fd, FuncIndex = fi, Code = fd.Code, Regs = regs, ArgSlots = argSlots, Sp = stackBase,
			Captures = caps, This = self, HasThis = hasSelf, ClassArgs = classArgs, MethodArgs = methodArgs,
			Caller = caller, Depth = depth, ReturnsThis = returnsThis,
		};
	}

	/// <summary>
	/// Общая точка входа вызова. Для нативных и async-функций результат возвращается сразу (Frame = null),
	/// для обычных — возвращается готовый фрейм, который должен исполнить вызывающий.
	/// </summary>
	internal Frame? Enter(Frame? caller, int fi, FunctionDef fd, KValue[] src, int start, int argc, KValue self, bool hasSelf,
		KCell[] caps, KType[] classArgs, KType[] methodArgs, out KValue result)
	{
		if (fd.Flags.HasFlag(MemberFlags.Native))
		{
			int n = hasSelf ? argc + 1 : argc;
			var buf = new KValue[n];
			if (hasSelf) buf[0] = self;
			if (argc > 0) Array.Copy(src, start, buf, hasSelf ? 1 : 0, argc);
			var inv = GetNative(fd.NativeKey ?? throw new KernPanic($"у нативной функции {fd.Name} нет NativeKey"));
			result = inv(this, buf, methodArgs.Length == 0 ? classArgs : [.. classArgs, .. methodArgs]);
			return null;
		}
		var f = NewFrame(fd, fi, src, start, argc, self, hasSelf, caps, classArgs, methodArgs, caller);
		if (fd.Flags.HasFlag(MemberFlags.Async))
		{
			result = KValue.Obj(StartCoroutine(f));
			return null;
		}
		result = default;
		return f;
	}

	/// <summary>Аргументы типа класса для вызова реализации impl на объекте obj.</summary>
	internal KType[] ClassArgsFor(int impl, KObject obj)
	{
		int owner = _funcs[impl].Owner;
		if (owner < 0 || Module.Types[owner].GenericParams.Count == 0) return [];
		return TypeChecks.AncestorArgs(obj.Type, owner) ?? [];
	}

	/// <summary>Реализация виртуального метода для объекта; -1, если реализации нет.</summary>
	internal int ResolveVirtual(KObject o, int slotFunc)
	{
		int root = Info.Roots[slotFunc];
		if (Info.Classes[o.Type.DefIndex].Vtable.TryGetValue(root, out int impl) && _funcs[impl].Code.Length + (_funcs[impl].Flags.HasFlag(MemberFlags.Native) ? 1 : 0) > 0)
			return impl;
		var self = _funcs[slotFunc];
		return self.Code.Length > 0 || self.Flags.HasFlag(MemberFlags.Native) ? slotFunc : -1;
	}

	// ---------- публичный API вызовов ----------

	/// <summary>Вызвать значение-функцию (замыкание, ссылка на метод, привязанный метод) с аргументами.</summary>
	public KValue CallFunction(KValue function, ReadOnlySpan<KValue> args)
	{
		if (!_initialized) Initialize();
		if (function.Ref is not KFunction kf)
			throw new KernPanic("значение не является функцией: " + ValueOps.ToStr(function));
		if (kf.Delegate is { } d) return d(args.ToArray());
		int fi = kf.FuncIndex >= 0 ? kf.FuncIndex : Module.Functions.IndexOf(kf.Def!);
		if (_isInstance[fi] && !kf.HasThis)
		{
			if (args.Length == 0) throw new KernPanic("метод вызван без объекта");
			return InvokeFunction(fi, args[1..], args[0], true, kf.Captures, kf.ClassArgs, kf.MethodArgs);
		}
		return InvokeFunction(fi, args, kf.BoundThis, kf.HasThis, kf.Captures, kf.ClassArgs, kf.MethodArgs);
	}

	/// <summary>Вызвать функцию/статический метод по индексу.</summary>
	public KValue CallFunctionIndex(int functionIndex, params KValue[] args) =>
		InvokeFunction(functionIndex, args, default, false, [], [], []);

	/// <summary>Вызвать виртуальный метод объекта по индексу базового метода (слоту).</summary>
	public KValue CallMethod(KObject obj, int functionIndex, KValue[] args)
	{
		int impl = ResolveVirtual(obj, functionIndex);
		if (impl < 0)
		{
			if (obj.Native is KProxyState ps) return ps.Invoke(this, functionIndex, args);
			throw new KernPanic($"метод {_funcs[functionIndex].Name} не реализован в {obj.Type.Name}");
		}
		return InvokeFunction(impl, args, KValue.Obj(obj), true, [], ClassArgsFor(impl, obj), []);
	}

	/// <summary>Вызвать метод по имени (первая подходящая перегрузка по числу аргументов).</summary>
	public KValue CallMethodByName(KObject obj, string name, params KValue[] args)
	{
		var info = Info.Classes[obj.Type.DefIndex];
		if (info.MethodsByName.TryGetValue(name, out var list))
			foreach (int f in list)
				if (_funcs[f].Params.Count == args.Length)
					return CallMethod(obj, f, args);
		throw new KernPanic($"у {obj.Type.Name} нет метода {name} с {args.Length} арг.");
	}

	internal KValue InvokeFunction(int fi, ReadOnlySpan<KValue> args, KValue self, bool hasSelf, KCell[] caps, KType[] ca, KType[] ma)
	{
		var fd = _funcs[fi];
		var arr = args.ToArray();
		var frame = Enter(null, fi, fd, arr, 0, arr.Length, self, hasSelf, caps, ca, ma, out var res);
		return frame is null ? res : Execute(frame, out _);
	}

	/// <summary>Пользовательский to_str() (кроме сгенерированного компилятором).</summary>
	internal bool TryUserToStr(KObject k, out string s)
	{
		s = "";
		var info = Info.Classes[k.Type.DefIndex];
		if (!info.MethodsByName.TryGetValue("to_str", out var list)) return false;
		foreach (int f in list)
		{
			var fd = _funcs[f];
			if (fd.Params.Count != 0 || fd.Flags.HasFlag(MemberFlags.Generated) || fd.Flags.HasFlag(MemberFlags.Static)) continue;
			if (fd.Code.Length == 0 && !fd.Flags.HasFlag(MemberFlags.Native)) continue;
			var r = CallMethod(k, f, []);
			s = ValueOps.ToStr(r);
			return true;
		}
		return false;
	}
}
