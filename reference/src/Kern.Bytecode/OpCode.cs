namespace Kern.Bytecode;

/// <summary>
/// Стековая VM. Код — int[]: [opcode, operand...]. Число операндов указано рядом.
/// Типы аргументов — индексы в пулы KbcModule (Strings/TypeSigs/Constants/Functions/Types).
/// </summary>
public enum OpCode
{
	Nop,
	// стек
	Pop, Dup, Swap,
	// константы
	LdConst,        // constIdx
	LdNil, LdNone, LdTrue, LdFalse, LdVoid,
	LdI32,          // imm
	LdStr,          // stringIdx
	// локальные/аргументы/ячейки замыканий
	LdLoc, StLoc,   // slot
	LdArg, StArg,   // index
	LdCell, StCell, // captureIdx: чтение/запись захваченной ячейки
	NewCell,        // slot: создать ячейку для захватываемой переменной
	// поля/свойства/статика
	LdField, StField,       // fieldSlot ($self или объект на стеке)
	LdThis,                 // $
	LdStatic, StStatic,     // globalIdx
	GetProp, SetProp,       // nameIdx  (динамическая привязка по имени; обычно компилятор шлёт Call на геттер)
	// вызовы
	Call,           // funcIdx, argc, typeSigListIdx(-1 если нет generic-аргументов)
	CallVirt,       // funcIdx(слот базового метода), argc, typeSigListIdx
	CallNative,     // stringIdx(NativeKey), argc, typeSigListIdx
	CallClosure,    // argc
	CallDotnet,     // memberRefIdx (use dotnet), argc
	Ret, RetVoid,
	// объекты
	NewObj,         // typeIdx, ctorFuncIdx, argc, typeSigListIdx
	NewUnit,        // typeIdx, ctorFuncIdx, argc, typeSigListIdx
	CopyUnit,       // копия value-type
	NewList, NewArray, NewDict, NewTuple,   // elemTypeSigIdx / count
	MakeClosure,    // funcIdx, captureCount (значения ячеек на стеке)
	LdFunc,         // funcIdx — ссылка на метод
	BindMethod,     // nameIdx: obj.method без вызова → KFunc с привязанным $self
	// арифметика/сравнение (проверка точного числового типа в рантайме)
	Add, Sub, Mul, Div, Mod, Pow, Neg,
	BitAnd, BitOr, BitXor, BitNot, Shl, Shr,
	Eq, Ne, Lt, Gt, Le, Ge,
	RefEq, RefNe,           // === !==
	Not,
	CallOp,         // opSymbolStringIdx, 2: пользовательские/расширяющие операторы
	Concat,         // n строк → str
	Format,         // formatSpecStringIdx: форматирование значения f"${v:.2f}"
	// типы
	IsType, NotIsType,      // typeSigIdx
	CastType,               // typeSigIdx (as)
	TypeOf,                 // typeSigIdx → KTypeObject (reified, подставляет T из фрейма)
	// переходы
	Jmp, JmpIf, JmpIfNot,   // target
	JmpIfNil,               // target (не снимает значение со стека, если переход)
	JmpIfNotNilOrNone,      // target — Elvis ?:
	JmpIfNone,
	// управление
	Switch,         // n, targets... (по int)
	MatchType,      // typeSigIdx, targetIfNot  (дублирует значение)
	MatchEq,        // targetIfNot
	// коллекции/циклы
	IterInit, IterNext,     // IterNext endTarget
	RangeNew,       // flags: 1=inclusive, 2=has step
	ListAdd,        // для loop-коллектора
	Index, StIndex,
	In, NotIn,
	Destructure,    // count, flags(rest position)
	// async
	Await, AwaitAll,
	MakeTask,       // оборачивает значение в завершённую задачу
	// прочее
	Panic,
	AssertNonNil,   // smart-cast не требует рантайм-проверки; для явных `!!`
	Line,           // line (для трассировки; по -O2 вырезается)
	// --- дополнения VM (добавлены в конец, чтобы не менять нумерацию) ---
	LdCellRef,      // cellRef: положить саму KCell (для MakeClosure). cellRef>=0 — захват замыкания, <0 — локальная ячейка ~slot
	NewResult,      // isOk(1/0): value → Ok(value)/Err(value)
	ResultIsOk,     // result → bool
	ResultValue,    // result → внутреннее значение
	LdEnum,         // typeIdx, memberIdx: значение enum
	BindFunc,       // funcIdx: [receiver] → [KFunction с привязанным получателем] (ссылка на метод без вызова)
	CountIs,        // n, atLeast(0/1): [collection] → [bool]; длина списка/массива/кортежа равна n (или >= n)
	SaveSp,         // slot: запомнить глубину стека в локальной переменной (начало итерации цикла)
	RestoreSp,      // slot: вернуть глубину стека (stop/next изнутри выражения с недоеденными операндами)
}

public static class OpCodeInfo
{
	/// <summary>Число операндов у опкода (Switch — переменное: n + n целей).</summary>
	public static int OperandCount(OpCode op) => op switch
	{
		OpCode.LdConst or OpCode.LdI32 or OpCode.LdStr or OpCode.LdLoc or OpCode.StLoc or OpCode.LdArg or OpCode.StArg
			or OpCode.LdCell or OpCode.StCell or OpCode.NewCell or OpCode.LdField or OpCode.StField
			or OpCode.LdStatic or OpCode.StStatic or OpCode.GetProp or OpCode.SetProp
			or OpCode.CallClosure or OpCode.LdFunc or OpCode.BindMethod
			or OpCode.Jmp or OpCode.JmpIf or OpCode.JmpIfNot or OpCode.JmpIfNil or OpCode.JmpIfNotNilOrNone or OpCode.JmpIfNone
			or OpCode.MatchEq or OpCode.IsType or OpCode.NotIsType or OpCode.CastType or OpCode.TypeOf
			or OpCode.LdCellRef or OpCode.NewResult or OpCode.BindFunc or OpCode.SaveSp or OpCode.RestoreSp
			or OpCode.RangeNew or OpCode.Concat or OpCode.Format or OpCode.IterNext or OpCode.Line
			or OpCode.AwaitAll => 1,
		OpCode.Call or OpCode.CallVirt or OpCode.CallNative or OpCode.MakeClosure or OpCode.MatchType
			or OpCode.CallOp or OpCode.Destructure or OpCode.CountIs => op == OpCode.Call || op == OpCode.CallVirt || op == OpCode.CallNative ? 3 : 2,
		OpCode.NewList or OpCode.NewArray or OpCode.NewDict or OpCode.NewTuple or OpCode.LdEnum or OpCode.CallDotnet => 2,
		OpCode.NewObj or OpCode.NewUnit => 4,
		OpCode.Switch => -1,
		_ => 0,
	};
}
