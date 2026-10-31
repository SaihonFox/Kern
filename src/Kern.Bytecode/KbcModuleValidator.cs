namespace Kern.Bytecode;

/// <summary>
/// Проверка целостности модуля перед записью/исполнением: индексы пулов, границы кода,
/// цели переходов, слоты локальных. При нарушении — <see cref="KbcFormatException"/>.
/// </summary>
public static class KbcModuleValidator
{
	public static void Validate(KbcModule module)
	{
		ArgumentNullException.ThrowIfNull(module);
		if (string.IsNullOrWhiteSpace(module.Name))
			throw new KbcFormatException("у модуля пустое имя");

		if (module.EntryPoint != -1 && (uint)module.EntryPoint >= (uint)module.FunctionPool.Count)
			throw new KbcFormatException($"точка входа вне пула функций: {module.EntryPoint}");

		if (module.StaticInit != -1 && (uint)module.StaticInit >= (uint)module.FunctionPool.Count)
			throw new KbcFormatException($"индекс <static-init> вне пула функций: {module.StaticInit}");

		ValidateTypes(module);

		for (int i = 0; i < module.FieldPool.Count; i++)
			ValidateField(module, module.FieldPool[i], $"поле модуля #{i}");

		for (int i = 0; i < module.PropertyPool.Count; i++)
			ValidateProperty(module, module.PropertyPool[i], $"свойство модуля #{i}");

		for (int i = 0; i < module.FunctionPool.Count; i++)
			ValidateFunction(module, module.FunctionPool[i], i);
	}

	private static void ValidateTypes(KbcModule module)
	{
		HashSet<string> names = new(StringComparer.Ordinal);
		for (int i = 0; i < module.TypePool.Count; i++)
		{
			TypeDef type = module.TypePool[i];
			if (string.IsNullOrWhiteSpace(type.Name))
				throw new KbcFormatException($"тип #{i}: пустое имя");
			if (string.IsNullOrWhiteSpace(type.FullName))
				throw new KbcFormatException($"тип '{type.Name}': пустое полное имя");
			if (!names.Add(type.FullName))
				throw new KbcFormatException($"повторяющееся полное имя типа: {type.FullName}");

			for (int f = 0; f < type.Fields.Count; f++)
				ValidateField(module, type.Fields[f], $"поле {type.FullName}.{type.Fields[f].Name}");

			for (int p = 0; p < type.Properties.Count; p++)
				ValidateProperty(module, type.Properties[p], $"свойство {type.FullName}.{type.Properties[p].Name}");

			foreach (int method in type.Methods)
				ValidateFunctionIndex(module, method, $"метод типа {type.FullName}");
			foreach (int ctor in type.Ctors)
				ValidateFunctionIndex(module, ctor, $"конструктор типа {type.FullName}");
		}
	}

	private static void ValidateFunctionIndex(KbcModule module, int index, string where)
	{
		if ((uint)index >= (uint)module.FunctionPool.Count)
			throw new KbcFormatException($"{where}: индекс функции вне пула: {index}");
	}

	private static void ValidateField(KbcModule module, FieldDef field, string where)
	{
		if (string.IsNullOrWhiteSpace(field.Name))
			throw new KbcFormatException($"{where}: пустое имя");
		if (field.Slot < 0)
			throw new KbcFormatException($"{where}: отрицательный слот {field.Slot}");
	}

	private static void ValidateProperty(KbcModule module, PropertyDef property, string where)
	{
		if (string.IsNullOrWhiteSpace(property.Name))
			throw new KbcFormatException($"{where}: пустое имя");
		if (property.Getter != -1)
			ValidateFunctionIndex(module, property.Getter, $"{where}.get");
		if (property.Setter != -1)
			ValidateFunctionIndex(module, property.Setter, $"{where}.set");
		if (property.Init != -1)
			ValidateFunctionIndex(module, property.Init, $"{where}.init");
	}

	private static void ValidateFunction(KbcModule module, FunctionDef function, int index)
	{
		string where = $"функция #{index} '{function.Name}'";
		if (string.IsNullOrWhiteSpace(function.Name))
			throw new KbcFormatException($"функция #{index}: пустое имя");
		if (function.OwnerType != -1 && (uint)function.OwnerType >= (uint)module.TypePool.Count)
			throw new KbcFormatException($"{where}: владелец вне пула типов: {function.OwnerType}");
		if (function.MaxStack < 0)
			throw new KbcFormatException($"{where}: отрицательный MaxStack");
		if (function.NativeTypeArgs != -1 && (uint)function.NativeTypeArgs >= (uint)module.TypeSigListPool.Count)
			throw new KbcFormatException($"{where}: индекс нативных type-аргументов вне пула: {function.NativeTypeArgs}");
		if (function.IsNative)
		{
			if (string.IsNullOrWhiteSpace(function.NativeKey))
				throw new KbcFormatException($"{where}: у нативной функции пустой NativeKey");
			if (function.LocalCount != 0)
				throw new KbcFormatException($"{where}: у нативной функции не должно быть локальных слотов");
			return;
		}
		if (function.NativeKey is not null)
			throw new KbcFormatException($"{where}: NativeKey задан без флага Native");
		if (function.LocalCount < function.ParamCount)
			throw new KbcFormatException($"{where}: LocalCount {function.LocalCount} меньше числа параметров {function.ParamCount}");
		if (function.Code.Length == 0)
			throw new KbcFormatException($"{where}: пустой код");

		int[] code = function.Code;
		int pc = 0;
		int[] boundaries = new int[code.Length];
		int boundaryCount = 0;
		while (pc < code.Length)
		{
			boundaries[boundaryCount++] = pc;
			int raw = code[pc];
			if (!OpCodeInfo.IsDefined(raw))
				throw new KbcFormatException($"{where}: неизвестный опкод {raw} по смещению {pc}");
			OpCode op = (OpCode)raw;
			int operands = OpCodeInfo.OperandCount(op);
			if (operands > 0 && pc + operands >= code.Length)
				throw new KbcFormatException($"{where}: обрезанная инструкция {op} по смещению {pc}");

			ValidateOperands(module, function, op, pc, where);
			pc += 1 + operands;
		}

		for (int i = 0; i < boundaryCount; i++)
		{
			int at = boundaries[i];
			OpCode op = (OpCode)code[at];
			if (!OpCodeInfo.IsJump(op)) continue;
			int target = code[at + 1];
			bool found = false;
			for (int b = 0; b < boundaryCount; b++)
			{
				if (boundaries[b] == target)
				{
					found = true;
					break;
				}
			}
			if (!found)
				throw new KbcFormatException($"{where}: переход на недопустимую цель {target} (смещение {at})");
		}

		foreach (LineEntry line in function.LineTable)
			if (line.Offset < 0 || line.Offset > code.Length)
				throw new KbcFormatException($"{where}: смещение таблицы строк вне кода: {line.Offset}");
	}

	private static void ValidateOperands(KbcModule module, FunctionDef function, OpCode op, int at, string where)
	{
		int[] code = function.Code;
		int arg = op switch
		{
			OpCode.PushConst or OpCode.PushI32 or OpCode.LoadLocal or OpCode.StoreLocal
				or OpCode.Jmp or OpCode.JmpIf or OpCode.JmpIfNot
				or OpCode.SaveSp or OpCode.RestoreSp
				or OpCode.NewObject or OpCode.LoadField or OpCode.StoreField
				or OpCode.LoadStatic or OpCode.StoreStatic
				or OpCode.NewTuple or OpCode.MakeFunc or OpCode.LoadCapture
				or OpCode.CallValue or OpCode.TypeOfSig or OpCode.IsType
				or OpCode.NewList or OpCode.NewArray or OpCode.NewDict
				or OpCode.MakeClosure or OpCode.Call or OpCode.TupleGet => code[at + 1],
			_ => -1,
		};

		switch (op)
		{
			case OpCode.PushConst:
				if ((uint)arg >= (uint)module.ConstPool.Count)
					throw new KbcFormatException($"{where}: индекс константы вне пула: {arg}");
				break;
			case OpCode.LoadLocal or OpCode.StoreLocal or OpCode.SaveSp or OpCode.RestoreSp:
				if ((uint)arg >= (uint)function.LocalCount)
					throw new KbcFormatException($"{where}: слот локальной вне кадра: {arg}");
				break;
			case OpCode.LoadCapture:
				if (arg < 0)
					throw new KbcFormatException($"{where}: отрицательный индекс захвата: {arg}");
				break;
			case OpCode.Jmp or OpCode.JmpIf or OpCode.JmpIfNot:
				// цели проверяются после обхода кода (нужны все границы инструкций)
				break;
			case OpCode.Call:
				{
					int argc = code[at + 2];
					if ((uint)arg >= (uint)module.FunctionPool.Count)
						throw new KbcFormatException($"{where}: индекс вызываемой функции вне пула: {arg}");
					FunctionDef callee = module.FunctionPool[arg];
					if (argc != callee.ParamCount)
						throw new KbcFormatException(
							$"{where}: у вызова '{callee.Name}' argc={argc}, а параметров {callee.ParamCount}");
					break;
				}
			case OpCode.NewObject:
				ValidateTypeIndex(module, arg, where, "типа объекта");
				break;
			case OpCode.LoadField or OpCode.StoreField:
				// Слот проверяется у объекта во время исполнения: поле можно читать и писать
				// из любой функции, а не только из метода владельца (spec §4).
				if (arg < 0)
					throw new KbcFormatException($"{where}: отрицательный слот поля: {arg}");
				break;
			case OpCode.TupleGet:
				// Индекс элемента кортежа; арность известна только значению во время
				// исполнения, поэтому здесь проверяется лишь неотрицательность.
				if (arg < 0)
					throw new KbcFormatException($"{where}: отрицательный индекс элемента кортежа: {arg}");
				break;
			case OpCode.LoadStatic or OpCode.StoreStatic:
				if ((uint)arg >= (uint)module.FieldPool.Count)
					throw new KbcFormatException($"{where}: индекс глобального поля вне пула: {arg}");
				break;
			case OpCode.NewList or OpCode.NewArray:
				ValidateSigIndex(module, arg, where, "элемента коллекции");
				if (code[at + 2] < 0)
					throw new KbcFormatException($"{where}: отрицательное число элементов коллекции: {code[at + 2]}");
				break;
			case OpCode.NewDict:
				ValidateSigIndex(module, arg, where, "ключа словаря");
				ValidateSigIndex(module, code[at + 2], where, "значения словаря");
				break;
			case OpCode.NewTuple:
				if ((uint)arg >= (uint)module.TypeSigListPool.Count)
					throw new KbcFormatException($"{where}: индекс списка подписей вне пула: {arg}");
				foreach (int sig in module.TypeSigListPool[arg])
					ValidateSigIndex(module, sig, where, "элемента кортежа");
				break;
			case OpCode.MakeFunc:
				ValidateFunctionIndex(module, arg, where);
				break;
			case OpCode.MakeClosure:
				{
					ValidateFunctionIndex(module, arg, where);
					int captures = code[at + 2];
					FunctionDef callee = module.FunctionPool[arg];
					if (captures < 0 || captures > callee.LocalCount - callee.ParamCount)
						throw new KbcFormatException($"{where}: недопустимое число захватов {captures} у '{callee.Name}'");
					break;
				}
			case OpCode.CallValue:
				if (arg < 0)
					throw new KbcFormatException($"{where}: отрицательное число аргументов вызова функции-значения");
				break;
			case OpCode.TypeOfSig or OpCode.IsType:
				ValidateSigIndex(module, arg, where, "типа");
				break;
			case OpCode.Slice:
				// B5b2: операнд — только флаг включительного конца (1/0).
				if (code[at + 1] is not (0 or 1))
					throw new KbcFormatException(
						$"{where}: флаг конца среза должен быть 0 или 1, получено {code[at + 1]}");
				break;
			case OpCode.ValueHash or OpCode.ValueCompare
				or OpCode.ValueHashFields or OpCode.ValueCompareFields
				or OpCode.CloneObject
				or OpCode.Repeat:
				// B6b1/B8a/B8c + Repeat: операндов нет — проверять нечего.
				break;
		}
	}

	private static void ValidateTypeIndex(KbcModule module, int index, string where, string what)
	{
		if ((uint)index >= (uint)module.TypePool.Count)
			throw new KbcFormatException($"{where}: индекс {what} вне пула: {index}");
	}

	private static void ValidateSigIndex(KbcModule module, int index, string where, string what)
	{
		if ((uint)index >= (uint)module.TypeSigPool.Count)
			throw new KbcFormatException($"{where}: индекс подписи {what} вне пула: {index}");
	}
}
