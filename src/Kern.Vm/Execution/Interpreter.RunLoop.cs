using Kern.Bytecode;
using Kern.Vm.Async;
using Kern.Vm.Native;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>Основной цикл исполнения инструкций VM (spec §16).</summary>
public sealed partial class Interpreter
{
	private void RunLoop(ExecutionState state)
	{
		OperandStack stack = state.Stack;
		Frame[] frames = state.Frames;
		int frameCount = state.FrameCount;
		FunctionDef function = state.Function;
		int[] code = state.Code;
		KValue[] locals = state.Locals;
		KValue[]? captures = state.Captures;
		int pc = state.Pc;
		int stackBase = state.StackBase;
		int instruction = 0;

		try
		{
			while (true)
			{
				instruction = pc;
				OpCode op = (OpCode)code[pc++];
				switch (op)
				{
					// ---------- стек ----------
					case OpCode.Nop:
						break;
					case OpCode.Pop:
						stack.Pop();
						break;
					case OpCode.Dup:
						stack.Push(stack.Peek());
						break;
					case OpCode.Swap:
						stack.Swap();
						break;

					// ---------- константы ----------
					case OpCode.PushConst:
						stack.Push(ToValue(_module.ConstPool[code[pc++]]));
						break;
					case OpCode.PushI32:
						stack.Push(KValue.I32(code[pc++]));
						break;
					case OpCode.PushI64:
						stack.Push(KValue.I64(BitsToInt64(code[pc], code[pc + 1])));
						pc += 2;
						break;
					case OpCode.PushF64:
						stack.Push(KValue.F64(BitConverter.Int64BitsToDouble(BitsToInt64(code[pc], code[pc + 1]))));
						pc += 2;
						break;
					case OpCode.PushTrue:
						stack.Push(KValue.True);
						break;
					case OpCode.PushFalse:
						stack.Push(KValue.False);
						break;
					case OpCode.PushNil:
						stack.Push(KValue.Nil);
						break;
					case OpCode.PushNone:
						stack.Push(KValue.None);
						break;
					case OpCode.PushVoid:
						stack.Push(KValue.Void);
						break;

					// ---------- локальные ----------
					case OpCode.LoadLocal:
						stack.Push(locals[code[pc++]]);
						break;
					case OpCode.StoreLocal:
						locals[code[pc++]] = stack.Pop();
						break;

					// ---------- арифметика ----------
					case OpCode.Add or OpCode.Sub or OpCode.Mul or OpCode.Div or OpCode.Mod or OpCode.Pow:
						{
							KValue right = stack.Pop();
							KValue left = stack.Pop();
							stack.Push(op switch
							{
								OpCode.Add => KValueMath.Add(left, right),
								OpCode.Sub => KValueMath.Sub(left, right),
								OpCode.Mul => KValueMath.Mul(left, right),
								OpCode.Div => KValueMath.Div(left, right),
								OpCode.Mod => KValueMath.Mod(left, right),
								_ => KValueMath.Pow(left, right),
							});
							break;
						}
					case OpCode.Neg:
						stack.Push(KValueMath.Neg(stack.Pop()));
						break;

					// ---------- битовые операции ----------
					case OpCode.BitAnd or OpCode.BitOr or OpCode.BitXor or OpCode.Shl or OpCode.Shr:
						{
							KValue right = stack.Pop();
							KValue left = stack.Pop();
							stack.Push(op switch
							{
								OpCode.BitAnd => KValueMath.BitAnd(left, right),
								OpCode.BitOr => KValueMath.BitOr(left, right),
								OpCode.BitXor => KValueMath.BitXor(left, right),
								OpCode.Shl => KValueMath.Shl(left, right),
								_ => KValueMath.Shr(left, right),
							});
							break;
						}
					case OpCode.BitNot:
						stack.Push(KValueMath.BitNot(stack.Pop()));
						break;

					// ---------- сравнение и логика ----------
					case OpCode.Eq or OpCode.Ne or OpCode.Lt or OpCode.Le or OpCode.Gt or OpCode.Ge:
						{
							KValue right = stack.Pop();
							KValue left = stack.Pop();
							bool result = op switch
							{
								OpCode.Eq => KValueMath.AreEqual(left, right),
								OpCode.Ne => !KValueMath.AreEqual(left, right),
								OpCode.Lt => KValueMath.LessThan(left, right),
								OpCode.Le => KValueMath.LessOrEqual(left, right),
								OpCode.Gt => KValueMath.GreaterThan(left, right),
								_ => KValueMath.GreaterOrEqual(left, right),
							};
							stack.Push(KValue.Bool(result));
							break;
						}
					case OpCode.Not:
						stack.Push(KValueMath.Not(stack.Pop()));
						break;
					case OpCode.And or OpCode.Or:
						{
							KValue right = stack.Pop();
							KValue left = stack.Pop();
							stack.Push(op == OpCode.And ? KValueMath.And(left, right) : KValueMath.Or(left, right));
							break;
						}

					// ---------- протоколы hash/compare (B6b1) ----------
					case OpCode.ValueHash:
						stack.Push(KValue.U64(KValueMath.StructuralHash64(stack.Pop())));
						break;
					case OpCode.ValueCompare:
						{
							KValue right = stack.Pop();
							KValue left = stack.Pop();
							stack.Push(KValue.I32(KValueMath.StructuralCompare(left, right)));
							break;
						}

					// ---------- производные hash/compare по полям (B8a) ----------
					case OpCode.ValueHashFields:
						stack.Push(KValue.U64(KValueMath.FieldHash64(stack.Pop())));
						break;
					case OpCode.ValueCompareFields:
						{
							KValue right = stack.Pop();
							KValue left = stack.Pop();
							stack.Push(KValue.I32(KValueMath.FieldCompare(left, right)));
							break;
						}

					// ---------- производный clone (B8c) ----------
					case OpCode.CloneObject:
						{
							KValue value = stack.Pop();
							if (value.Ref is not KObject obj)
								throw new KernPanic($"CloneObject: ожидался объект, получено {value.TypeName}");
							stack.Push(KValue.Obj(obj.CloneShallow()));
							break;
						}

					// ---------- повтор строки/символа (spec §3) ----------
					case OpCode.Repeat:
						{
							KValue right = stack.Pop();
							KValue left = stack.Pop();
							stack.Push(KValueMath.Repeat(left, right));
							break;
						}

					// ---------- ячейки замыканий ----------
					case OpCode.NewCell:
						stack.Push(KValue.Obj(new KCell(stack.Pop())));
						break;
					case OpCode.LoadCell:
						stack.Push(KCell.Require(stack.Pop(), "LoadCell").Value);
						break;
					case OpCode.StoreCell:
						{
							KValue stored = stack.Pop();
							KCell.Require(stack.Pop(), "StoreCell").Value = stored;
							break;
						}

					// ---------- преобразования ----------
					case OpCode.ConvI8 or OpCode.ConvI16 or OpCode.ConvI32 or OpCode.ConvI64
						or OpCode.ConvU8 or OpCode.ConvU16 or OpCode.ConvU32 or OpCode.ConvU64
						or OpCode.ConvF16 or OpCode.ConvF32 or OpCode.ConvF64
						or OpCode.ConvChar or OpCode.ConvBool:
						{
							ValueKind target = op switch
							{
								OpCode.ConvI8 => ValueKind.I8,
								OpCode.ConvI16 => ValueKind.I16,
								OpCode.ConvI32 => ValueKind.I32,
								OpCode.ConvI64 => ValueKind.I64,
								OpCode.ConvU8 => ValueKind.U8,
								OpCode.ConvU16 => ValueKind.U16,
								OpCode.ConvU32 => ValueKind.U32,
								OpCode.ConvU64 => ValueKind.U64,
								OpCode.ConvF16 => ValueKind.F16,
								OpCode.ConvF32 => ValueKind.F32,
								OpCode.ConvF64 => ValueKind.F64,
								OpCode.ConvChar => ValueKind.Char,
								_ => ValueKind.Bool,
							};
							stack.Push(KValueMath.Convert(stack.Pop(), target));
							break;
						}

					// ---------- переходы ----------
					case OpCode.Jmp:
						pc = code[pc];
						break;
					case OpCode.JmpIf:
						{
							int target = code[pc++];
							if (RequireBool(stack.Pop())) pc = target;
							break;
						}
					case OpCode.JmpIfNot:
						{
							int target = code[pc++];
							if (!RequireBool(stack.Pop())) pc = target;
							break;
						}

					// ---------- вызовы ----------
					case OpCode.Call or OpCode.CallVirtual or OpCode.CallBase:
						{
							int calleeIndex = code[pc++];
							int argc = code[pc++];
							FunctionDef callee = _module.FunctionPool[calleeIndex];
							if (op == OpCode.CallVirtual)
							{
								// Получатель — первый аргумент, он же слот 0 кадра вызываемого.
								// При недопустимой базе кадра резолв пропускаем: BeginCall ниже
								// сообщит об ошибке тем же путём, что и для Call.
								KValue receiver = argc > 0 && stack.Count - argc >= stackBase
									? stack.At(stack.Count - argc)
									: KValue.None;
								callee = ResolveVirtualTarget(callee, receiver);
							}
							else if (op == OpCode.CallBase &&
								argc > 0 && stack.Count - argc >= stackBase &&
								stack.At(stack.Count - argc).Ref is KObject baseReceiver &&
								callee.OwnerType >= 0 &&
								baseReceiver.IsConstructed(_module.TypePool[callee.OwnerType]))
							{
								// Ромб наследования: конструктор общего предка уже выполнился
								// на этом объекте — повторный вызов пропускается (объект один).
								stack.Truncate(stack.Count - argc);
								stack.Push(KValue.Void);
								break;
							}

							// Прокси (mock/http_client.proxy) и async-вызов — отдельные помощники.
							if (TryInvokeProxy(callee, argc, ref stack))
								break;
							if (TryStartAsyncCall(callee, argc, ref stack))
								break;

							// Ромб: отметить выполняемый конструктор, чтобы повторный CallBase
							// того же типа не инициализировал общий подобъект второй раз.
							if ((callee.Flags & MemberFlags.Ctor) != 0 &&
								callee.OwnerType >= 0 &&
								argc > 0 && stack.Count - argc >= stackBase &&
								stack.At(stack.Count - argc).Ref is KObject constructed)
							{
								constructed.MarkConstructed(_module.TypePool[callee.OwnerType]);
							}

							CallStart start = BeginCall(
								callee, argc, function, locals, captures, pc, stackBase,
								ref stack, ref frames, ref frameCount);
							if (start.Function is not { } called)
								break;
							function = called;
							code = function.Code;
							locals = start.Locals!;
							captures = null;
							pc = 0;
							stackBase = start.StackBase;
							break;
						}
					case OpCode.Ret:
						{
							KValue result = stack.Pop();
							if (stack.Count != stackBase)
								throw new KernPanic(
									$"нарушен баланс стека в функции '{function.Name}': глубина {stack.Count}, ожидалось {stackBase}");
							// Асинхронная функция возвращает вызывающему готовую задачу (spec §M5):
							// await внутри неё уже припарковал/возобновил это состояние.
							if ((function.Flags & MemberFlags.Async) != 0)
								result = KValue.Obj(KTask.FromResult(result));
							if (frameCount == 0)
							{
								state.Complete(result);
								return;
							}

							Frame caller = frames[--frameCount];
							// Кадр вызывающего помнит окно, открытое этим вызовом: конструктор
							// вернулся — значение unit! запечатано.
							caller.Construction?.EndConstruction();
							function = caller.Function;
							code = function.Code;
							locals = caller.Locals;
							captures = caller.Captures;
							pc = caller.Pc;
							stackBase = caller.StackBase;
							stack.Push(result);
							break;
						}
					case OpCode.Stop:
						state.Complete(KValue.Void);
						return;

					// ---------- глубина стека ----------
					case OpCode.SaveSp:
						locals[code[pc++]] = KValue.I32(stack.Count);
						break;
					case OpCode.RestoreSp:
						{
							int slot = code[pc++];
							KValue saved = locals[slot];
							if (saved.Kind != ValueKind.I32)
								throw new KernPanic($"RestoreSp: слот {slot} не содержит i32 (там {saved.TypeName})");
							int depth = saved.AsI32;
							if (depth < stackBase || depth > stack.Count)
								throw new KernPanic($"RestoreSp: недопустимая глубина стека {depth}");
							stack.Truncate(depth);
							break;
						}

					// ---------- объекты, поля и статика ----------
					case OpCode.NewObject:
						{
							int typeIndex = code[pc++];
							TypeDef definition = _module.TypePool[typeIndex];
							KClassType objectType = _types.Named(definition);
							KValue[] fields = new KValue[definition.Fields.Count];
							Array.Fill(fields, KValue.None);
							stack.Push(KValue.Obj(new KObject(objectType, fields)));
							break;
						}
					case OpCode.NewObjectGeneric:
						PushNewObjectGeneric(code[pc++], code[pc++], ref stack);
						break;
					case OpCode.CopyUnit:
						{
							KValue value = stack.Pop();
							if (value.Ref is not KObject { IsUnit: true } unit)
								throw new KernPanic($"CopyUnit: ожидался unit, получено {value.TypeName}");
							stack.Push(KValue.Obj(unit.CopyUnit()));
							break;
						}
					case OpCode.LoadField:
						{
							int slot = code[pc++];
							KValue receiver = stack.Pop();
							if (receiver.Ref is not KObject instance)
								throw new KernPanic($"LoadField: ожидался объект, получено {receiver.TypeName}");
							stack.Push(instance.GetField(slot));
							break;
						}
					case OpCode.StoreField:
						{
							int slot = code[pc++];
							KValue value = stack.Pop();
							KValue receiver = stack.Pop();
							if (receiver.Ref is not KObject instance)
								throw new KernPanic($"StoreField: ожидался объект, получено {receiver.TypeName}");
							instance.SetField(slot, value);
							stack.Push(KValue.Void);
							break;
						}
					case OpCode.LoadStatic:
						{
							int fieldIndex = code[pc++];
							stack.Push(_statics[fieldIndex]);
							break;
						}
					case OpCode.StoreStatic:
						{
							int fieldIndex = code[pc++];
							_statics[fieldIndex] = stack.Pop();
							stack.Push(KValue.Void);
							break;
						}

					// ---------- коллекции и кортежи ----------
					case OpCode.NewList:
						{
							KType elementType = _types.FromSig(_module.TypeSigPool[code[pc++]]);
							int count = code[pc++];
							stack.Push(KValue.Obj(new KList(elementType, PopItems(ref stack, count))));
							break;
						}
					case OpCode.ListAppend:
						{
							// Стек: [list, value]; список остаётся на стеке после добавления.
							KValue value = stack.Pop();
							KValue receiver = stack.Pop();
							if (receiver.Ref is not KList list)
								throw new KernPanic($"ListAppend: ожидался list, получено {receiver.TypeName}");
							list.Add(value);
							stack.Push(receiver);
							break;
						}
					case OpCode.ListAppendRange:
						{
							// Стек: [list, source]; элементы копируются, исходная коллекция не меняется.
							KValue source = stack.Pop();
							KValue receiver = stack.Pop();
							if (receiver.Ref is not KList list)
								throw new KernPanic($"ListAppendRange: ожидался list, получено {receiver.TypeName}");
							switch (source.Ref)
							{
								case KList items:
									foreach (KValue item in items.Items)
										list.Add(item);
									break;
								case KArray array:
									for (var index = 0; index < array.Length; index++)
										list.Add(array[index]);
									break;
								default:
									throw new KernPanic(
										$"ListAppendRange: ожидался list или array, получено {source.TypeName}");
							}

							stack.Push(receiver);
							break;
						}
					case OpCode.ListLen:
						{
							// B4b1: длина list/array как i32 для проверки паттерна списка.
							KValue receiver = stack.Pop();
							stack.Push(receiver.Ref switch
							{
								KList list => KValue.I32(list.Count),
								KArray array => KValue.I32(array.Length),
								_ => throw new KernPanic($"ListLen: ожидался list или array, получено {receiver.TypeName}")
							});
							break;
						}
					case OpCode.ListGet:
						{
							// B4b1/B5b1: стек [list/array, индекс i32]; выход — элемент.
							// Паттерны списков всегда передают неотрицательный индекс; отрицательный
							// (B5b1, spec round 27) считается с конца.
							KValue indexValue = stack.Pop();
							KValue receiver = stack.Pop();
							if (indexValue.Kind != ValueKind.I32)
								throw new KernPanic($"ListGet: ожидался индекс i32, получено {indexValue.TypeName}");
							int index = indexValue.AsI32;
							stack.Push(receiver.Ref switch
							{
								KList list => list[NormalizeIndex(index, list.Count)],
								KArray array => array[NormalizeIndex(index, array.Length)],
								_ => throw new KernPanic($"ListGet: ожидался list или array, получено {receiver.TypeName}")
							});
							break;
						}
					case OpCode.ListSet:
						{
							// spec §5: стек [list/array, индекс i32, значение]; запись на месте,
							// на стек ничего не остаётся. Отрицательный индекс считается с конца.
							KValue value = stack.Pop();
							KValue indexValue = stack.Pop();
							KValue receiver = stack.Pop();
							if (indexValue.Kind != ValueKind.I32)
								throw new KernPanic($"ListSet: ожидался индекс i32, получено {indexValue.TypeName}");
							int index = indexValue.AsI32;
							switch (receiver.Ref)
							{
								case KList list:
									list[NormalizeIndex(index, list.Count)] = value;
									break;
								case KArray array:
									array[NormalizeIndex(index, array.Length)] = value;
									break;
								default:
									throw new KernPanic(
										$"ListSet: ожидался list или array, получено {receiver.TypeName}");
							}
							break;
						}
					case OpCode.StrGet:
						{
							// B5b2: стек [str, индекс i32]; выход — char. Отрицательный индекс
							// считается с конца, выход за границы — паника (spec round 27).
							KValue indexValue = stack.Pop();
							KValue receiver = stack.Pop();
							if (indexValue.Kind != ValueKind.I32)
								throw new KernPanic($"StrGet: ожидался индекс i32, получено {indexValue.TypeName}");
							if (receiver.Kind != ValueKind.Str)
								throw new KernPanic($"StrGet: ожидалась строка, получено {receiver.TypeName}");
							string text = receiver.AsStr;
							int index = NormalizeIndex(indexValue.AsI32, text.Length);
							if ((uint)index >= (uint)text.Length)
								throw new KernPanic($"StrGet: индекс {indexValue.AsI32} вне длины {text.Length}");
							stack.Push(KValue.Char(text[index]));
							break;
						}
					case OpCode.ListSlice:
						{
							// B4b2: стек [list/array, начало i32, конец i32]; выход — новый list<T>
							// с элементами начало..конец (конец не включён). Источник не меняется.
							KValue endValue = stack.Pop();
							KValue startValue = stack.Pop();
							KValue receiver = stack.Pop();
							if (startValue.Kind != ValueKind.I32)
								throw new KernPanic($"ListSlice: ожидалось начало i32, получено {startValue.TypeName}");
							if (endValue.Kind != ValueKind.I32)
								throw new KernPanic($"ListSlice: ожидался конец i32, получено {endValue.TypeName}");
							int start = startValue.AsI32;
							int end = endValue.AsI32;
							stack.Push(receiver.Ref switch
							{
								KList list => KValue.Obj(new KList(
									list.ElementType,
									SliceItems(list.Count, index => list[index], start, end))),
								KArray array => KValue.Obj(new KList(
									array.ElementType,
									SliceItems(array.Length, index => array[index], start, end))),
								_ => throw new KernPanic($"ListSlice: ожидался list или array, получено {receiver.TypeName}")
							});
							break;
						}
					case OpCode.Slice:
						{
							// B5b2: стек [list/array/str, начало i32, конец i32], операнд — флаг
							// включительного конца (1/0). Отрицательные границы считаются с конца,
							// затем диапазон проверяется (паника) и копируется в новый list/str.
							bool inclusive = code[pc++] != 0;
							KValue endValue = stack.Pop();
							KValue startValue = stack.Pop();
							KValue receiver = stack.Pop();
							if (startValue.Kind != ValueKind.I32)
								throw new KernPanic($"Slice: ожидалось начало i32, получено {startValue.TypeName}");
							if (endValue.Kind != ValueKind.I32)
								throw new KernPanic($"Slice: ожидался конец i32, получено {endValue.TypeName}");
							int start = startValue.AsI32;
							int end = endValue.AsI32;
							if (receiver.Kind == ValueKind.Str)
							{
								string text = receiver.AsStr;
								(int from, int to) = NormalizeSlice(start, end, inclusive, text.Length);
								stack.Push(KValue.Str(text.Substring(from, to - from)));
								break;
							}

							switch (receiver.Ref)
							{
								case KList list:
									{
										(int from, int to) = NormalizeSlice(start, end, inclusive, list.Count);
										stack.Push(KValue.Obj(new KList(
											list.ElementType,
											SliceItems(list.Count, index => list[index], from, to))));
										break;
									}
								case KArray array:
									{
										(int from, int to) = NormalizeSlice(start, end, inclusive, array.Length);
										stack.Push(KValue.Obj(new KList(
											array.ElementType,
											SliceItems(array.Length, index => array[index], from, to))));
										break;
									}
								default:
									throw new KernPanic(
										$"Slice: ожидался list, array или str, получено {receiver.TypeName}");
							}

							break;
						}
					case OpCode.NewArray:
						{
							KType elementType = _types.FromSig(_module.TypeSigPool[code[pc++]]);
							int count = code[pc++];
							stack.Push(KValue.Obj(new KArray(elementType, PopItems(ref stack, count))));
							break;
						}
					case OpCode.NewDict:
						{
							KType keyType = _types.FromSig(_module.TypeSigPool[code[pc++]]);
							KType valueType = _types.FromSig(_module.TypeSigPool[code[pc++]]);
							stack.Push(KValue.Obj(new KDict(keyType, valueType)));
							break;
						}
					case OpCode.DictSet:
						{
							// B3b2: стек [dict, key, value]; словарь остаётся на стеке после записи.
							// Повтор ключа не диагностируется — последняя запись побеждает.
							KValue value = stack.Pop();
							KValue key = stack.Pop();
							KValue receiver = stack.Pop();
							if (receiver.Ref is not KDict dict)
								throw new KernPanic($"DictSet: ожидался dict, получено {receiver.TypeName}");
							dict.Set(key, value);
							stack.Push(receiver);
							break;
						}
					case OpCode.DictGet:
						{
							// B5b1: стек [dict, key]; выход — значение. Отсутствующий ключ —
							// паника внутри KDict.Get (spec round 27).
							KValue key = stack.Pop();
							KValue receiver = stack.Pop();
							if (receiver.Ref is not KDict dict)
								throw new KernPanic($"DictGet: ожидался dict, получено {receiver.TypeName}");
							stack.Push(dict.Get(key));
							break;
						}
					case OpCode.NewTuple:
						{
							int sigListIndex = code[pc++];
							int[] sigs = _module.TypeSigListPool[sigListIndex];
							KType[] itemTypes = [.. sigs.Select(index => _types.FromSig(_module.TypeSigPool[index]))];
							stack.Push(KValue.Obj(new KTuple(itemTypes, PopItems(ref stack, sigs.Length))));
							break;
						}
					case OpCode.TupleGet:
						{
							// spec §3: [кортеж] -> элемент по индексу; имя выбирает компилятор.
							int index = code[pc++];
							KValue receiver = stack.Pop();
							if (receiver.Ref is not KTuple tuple)
								throw new KernPanic($"TupleGet: ожидался кортеж, получено {receiver.TypeName}");
							stack.Push(tuple[index]);
							break;
						}

					// ---------- функции-значения и замыкания ----------
					case OpCode.MakeFunc:
						stack.Push(KValue.Obj(_module.FunctionPool[code[pc++]]));
						break;
					case OpCode.MakeClosure:
						{
							FunctionDef closureFunction = _module.FunctionPool[code[pc++]];
							int captureCount = code[pc++];
							stack.Push(KValue.Obj(new KClosure(closureFunction, PopItems(ref stack, captureCount))));
							break;
						}
					case OpCode.LoadCapture:
						{
							int slot = code[pc++];
							if (captures is null || (uint)slot >= (uint)captures.Length)
								throw new KernPanic($"LoadCapture: захват {slot} недоступен в функции '{function.Name}'");
							stack.Push(captures[slot]);
							break;
						}
					case OpCode.CallValue:
						{
							int argc = code[pc++];
							KValue[] arguments = new KValue[argc];
							for (int i = argc - 1; i >= 0; i--)
								arguments[i] = stack.Pop();
							KValue targetValue = stack.Pop();
							(FunctionDef target, KValue[]? targetCaptures) = targetValue.Ref switch
							{
								KClosure closure => (closure.Function, closure.Captures),
								FunctionDef targetFunction => (targetFunction, null),
								_ => throw new KernPanic($"CallValue: ожидалась функция, получено {targetValue.TypeName}"),
							};
							if (argc != target.ParamCount)
								throw new KernPanic(
									$"функция '{target.Name}' ожидает {target.ParamCount} аргументов, передано {argc}");
							if (target.IsNative)
							{
								stack.Push(InvokeNative(target, arguments));
								break;
							}
							if ((target.Flags & MemberFlags.Async) != 0)
							{
								stack.Push(KValue.Obj(StartAsync(targetValue, arguments)));
								break;
							}							int callBase = stack.Count;
							if (callBase < stackBase)
								throw new KernPanic($"вызов '{target.Name}': аргументы заходят ниже базы кадра");
							if (frameCount == frames.Length)
							{
								if (frames.Length >= MaxCallDepth)
									throw new KernPanic("переполнение стека вызовов");
								Array.Resize(ref frames, Math.Min(frames.Length * 2, MaxCallDepth));
							}
							if (frameCount >= MaxCallDepth)
								throw new KernPanic("переполнение стека вызовов");
							// CallValue окно создания не открывает: конструкторы не first-class.
							// Появится такой путь — здесь нужно открыть окно и записать его в кадр.
							frames[frameCount++] = new Frame(function, locals, captures, pc, stackBase, null);
							KValue[] calleeLocals = AllocateLocals(target);
							Array.Copy(arguments, calleeLocals, argc);
							function = target;
							code = target.Code;
							locals = calleeLocals;
							captures = targetCaptures;
							pc = 0;
							stackBase = callBase;
							break;
						}

					// ---------- типы и рефлексия ----------
					case OpCode.TypeOfSig:
						stack.Push(KValue.Obj(_types.FromSig(_module.TypeSigPool[code[pc++]])));
						break;
					case OpCode.TypeOfValue:
						stack.Push(KValue.Obj(InferType(stack.Pop())));
						break;
					case OpCode.IsType:
						{
							KType expected = _types.FromSig(_module.TypeSigPool[code[pc++]]);
							KType actual = InferType(stack.Pop());
							stack.Push(KValue.Bool(IsInstanceOf(actual, expected)));
							break;
						}

					// ---------- async ----------
					case OpCode.Await:
						{
							KValue awaited = stack.Pop();
							if (awaited.Ref is not KTask task)
								throw new KernPanic($"Await: ожидалась задача, получено {awaited.TypeName}");
							if (!task.IsCompleted)
							{
								state.Pc = pc;
								state.Stack = stack;
								state.Frames = frames;
								state.FrameCount = frameCount;
								state.Function = function;
								state.Code = code;
								state.Locals = locals;
								state.Captures = captures;
								state.StackBase = stackBase;
								task.OnCompleted(completed => Resume(state, completed), _scheduler);
								return;
							}
							PushTaskResult(task, ref stack);
							break;
						}

					// ---------- match ----------
					case OpCode.MatchFail:
						throw new KernPanic("match: ни одна ветвь не совпала");

					default:
						throw new KernPanic($"неизвестный опкод {(int)op} в функции '{function.Name}'");
				}
			}
		}
		catch (KernPanic panic)
		{
			panic.AddFrame(function.Name, instruction, function.LineAt(instruction));
			for (int i = frameCount - 1; i >= 0; i--)
			{
				Frame caller = frames[i];
				panic.AddFrame(caller.Function.Name, caller.Pc, caller.Function.LineAt(caller.Pc));
			}
			// Паника могла случиться в середине конструктора unit!: снимаем все окна
			// создания не вернувшихся кадров, иначе объект останется записываемым навсегда.
			CloseConstructionWindows(frames, frameCount);
			throw;
		}
	}
}
