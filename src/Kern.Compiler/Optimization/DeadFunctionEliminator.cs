using Kern.Bytecode;

namespace Kern.Compiler.Optimization;

/// <summary>
/// Удаление мёртвых функций приложения (spec §7/§16): от <c>main</c>, статического
/// инициализатора, публичных и <c>#[test]</c>-функций по графу вызовов. Методы типов
/// сохраняются (их можно вызвать через рефлексию/виртуальный диспатч), нативные члены
/// не трогаются; библиотечные модули (<c>EntryPoint &lt; 0</c>) не оптимизируются.
/// </summary>
public static class DeadFunctionEliminator
{
	public static KbcModule Eliminate(KbcModule module)
	{
		ArgumentNullException.ThrowIfNull(module);
		if (module.EntryPoint < 0 || module.FunctionPool.Count == 0)
			return module;

		var kept = new bool[module.FunctionPool.Count];
		var queue = new Queue<int>();
		void Root(int index)
		{
			if ((uint)index >= (uint)kept.Length || kept[index])
				return;
			kept[index] = true;
			queue.Enqueue(index);
		}

		Root(module.EntryPoint);
		Root(module.StaticInit);
		var virtualNames = new HashSet<(string Name, int Arity)>();
		var allFunctions = false;
		for (var index = 0; index < module.FunctionPool.Count; index++)
		{
			FunctionDef function = module.FunctionPool[index];
			if ((function.Flags & (MemberFlags.Pub | MemberFlags.Test)) != 0 ||
				function.OwnerType >= 0 ||
				(function.Flags & MemberFlags.Native) != 0)
				Root(index);
		}

		while (queue.Count > 0 && !allFunctions)
		{
			int index = queue.Dequeue();
			FunctionDef function = module.FunctionPool[index];
			if (function.Code.Length == 0)
				continue;
			var offset = 0;
			while (offset < function.Code.Length)
			{
				var op = (OpCode)function.Code[offset];
				int count = OpCodeInfo.OperandCount(op);
				switch (op)
				{
					case OpCode.Call or OpCode.CallGeneric or OpCode.CallBase or OpCode.TailCall or OpCode.MakeFunc or OpCode.MakeClosure:
					case OpCode.CallVirtual:
						int target = function.Code[offset + 1];
						if ((uint)target < (uint)kept.Length)
						{
							Root(target);
							if (op == OpCode.CallVirtual)
							{
								FunctionDef method = module.FunctionPool[target];
								virtualNames.Add((method.Name, method.ParamCount));
							}
						}
						break;
					case OpCode.CallValue:
						allFunctions = true;
						break;
				}

				offset += 1 + count;
			}
		}

		if (allFunctions)
			return module;

		// Виртуальный вызов может попасть в переопределение с тем же именем/арностью —
		// сохраняем все такие функции (методы типов уже сохранены выше, это страховка).
		for (var index = 0; index < module.FunctionPool.Count; index++)
		{
			FunctionDef function = module.FunctionPool[index];
			if (virtualNames.Contains((function.Name, function.ParamCount)))
				kept[index] = true;
		}

		var map = new Dictionary<int, int>();
		var pool = new List<FunctionDef>(module.FunctionPool.Count);
		for (var index = 0; index < module.FunctionPool.Count; index++)
		{
			if (!kept[index])
				continue;
			map[index] = pool.Count;
			pool.Add(module.FunctionPool[index]);
		}

		if (pool.Count == module.FunctionPool.Count)
			return module;

		foreach (FunctionDef function in pool)
			RemapFunctionIndices(function, map);

		module.EntryPoint = map[module.EntryPoint];
		if (module.StaticInit >= 0)
			module.StaticInit = map[module.StaticInit];
		foreach (TypeDef type in module.TypePool)
		{
			type.Methods = [.. type.Methods.Where(map.ContainsKey).Select(index => map[index])];
			type.Ctors = [.. type.Ctors.Where(map.ContainsKey).Select(index => map[index])];
			foreach (PropertyDef property in type.Properties)
			{
				if (map.TryGetValue(property.Getter, out int getter))
					property.Getter = getter;
				if (map.TryGetValue(property.Setter, out int setter))
					property.Setter = setter;
				if (map.TryGetValue(property.Init, out int init))
					property.Init = init;
			}
		}

		module.FunctionPool = pool;
		return module;
	}

	private static void RemapFunctionIndices(FunctionDef function, Dictionary<int, int> map)
	{
		var offset = 0;
		while (offset < function.Code.Length)
		{
			var op = (OpCode)function.Code[offset];
			int count = OpCodeInfo.OperandCount(op);
			if (count > 0 &&
				op is OpCode.Call or OpCode.CallGeneric or OpCode.CallVirtual or OpCode.CallBase or OpCode.TailCall
					or OpCode.MakeFunc or OpCode.MakeClosure)
			{
				int old = function.Code[offset + 1];
				if (map.TryGetValue(old, out int mapped))
					function.Code[offset + 1] = mapped;
			}
			offset += 1 + count;
		}
	}
}
