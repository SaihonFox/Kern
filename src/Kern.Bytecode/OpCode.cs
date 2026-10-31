namespace Kern.Bytecode;

/// <summary>
/// Опкоды стековой машины. Код функции — <c>int[]</c>: <c>[опкод, операнды...]</c>.
/// Нумерация фиксирована: новые опкоды добавляются только в конец перечисления,
/// существующие значения менять нельзя (они попадают в .kbc и кэши).
///
/// Контракт стека (spec §16): каждое выражение оставляет ровно одно значение
/// (void -> <see cref="OpCode.PushVoid"/>), каждый <c>Call</c> всегда кладёт результат,
/// а <see cref="OpCode.Ret"/> снимает с вершины ровно один результат.
/// </summary>
public enum OpCode
{
	// --- стек ---
	Nop = 0,
	Pop,            // нет операндов: снять верхнее значение
	Dup,            // продублировать верхнее значение
	Swap,           // поменять местами два верхних значения

	// --- константы и литералы ---
	PushConst,      // constIdx: KConst из ConstPool
	PushI32,        // imm: i32
	PushI64,        // immLo, immHi: i64 (младшее слово первым), little-endian
	PushF64,        // bitsLo, bitsHi: f64 по IEEE-754 (младшее слово первым)
	PushTrue,
	PushFalse,
	PushNil,
	PushNone,
	PushVoid,

	// --- локальные; аргументы занимают слоты 0..ParamCount-1 ---
	LoadLocal,      // slot
	StoreLocal,     // slot: снять верхнее значение и записать (ничего не кладёт)

	// --- арифметика ---
	Add,
	Sub,
	Mul,
	Div,
	Mod,
	Neg,            // унарный минус
	Pow,            // **

	// --- сравнение и логика ---
	Eq,
	Ne,
	Lt,
	Le,
	Gt,
	Ge,
	Not,
	And,            // неторопливый: && компилятор разворачивает в переходы
	Or,             // неторопливый: || компилятор разворачивает в переходы

	// --- преобразования ---
	ConvI8,
	ConvI16,
	ConvI32,
	ConvI64,
	ConvU8,
	ConvU16,
	ConvU32,
	ConvU64,
	ConvF16,
	ConvF32,
	ConvF64,
	ConvChar,
	ConvBool,

	// --- переходы; цель — абсолютный индекс опкода в Code ---
	Jmp,            // target
	JmpIf,          // target: снять bool, перейти если true
	JmpIfNot,       // target: снять bool, перейти если false

	// --- вызовы ---
	Call,           // funcIdx, argc: аргументы лежат на стеке по порядку (arg0 ниже); результат кладётся
	Ret,            // снять результат и вернуться из функции
	Stop,           // остановить VM; результат — Void

	// --- глубина стека (для stop/next внутри выражений) ---
	SaveSp,         // slot: записать текущую глубину стека в локальную (i32)
	RestoreSp,      // slot: восстановить глубину стека из локальной (i32)

	// --- объекты, поля и статика ---
	NewObject,      // typeIdx: создать KObject с полями типа (значения — none)
	CopyUnit,       // нет операндов: скопировать unit-объект с вершины (value-семантика)
	LoadField,      // slot: снять объект, положить его поле slot
	StoreField,     // slot: снять значение и объект, записать поле slot
	LoadStatic,     // fieldIdx (глобальное поле модуля)
	StoreStatic,    // fieldIdx: снять значение и записать глобал

	// --- коллекции и кортежи ---
	NewList,        // elemSigIdx, count: снять count элементов (первый ниже) и создать list
	NewArray,       // elemSigIdx, count: то же для array
	NewDict,        // keySigIdx, valSigIdx: создать пустой dict
	NewTuple,       // sigListIdx: снять N элементов и создать кортеж

	// --- функции-значения и замыкания ---
	MakeFunc,       // funcIdx: ссылка на функцию без захватов
	MakeClosure,    // funcIdx, captureCount: снять захваты и создать замыкание
	LoadCapture,    // slot: захват из текущего замыкания
	CallValue,      // argc: вызвать функцию-значение, лежащую ниже аргументов

	// --- типы и рефлексия ---
	TypeOfSig,      // sigIdx: reified KType как значение type
	TypeOfValue,    // нет операндов: тип значения с вершины
	IsType,         // sigIdx: снять значение, положить bool (проверка типа)

	// --- async ---
	Await,          // нет операндов: снять task<T>, припарковать фрейм или положить результат

	// --- match ---
	MatchFail,      // нет операндов: ни одна ветвь match не совпала — паника

	// --- битовые операции ---
	BitAnd,         // &: оба операнда целые одного вида, результат — вид операндов
	BitOr,          // |
	BitXor,         // ^
	BitNot,         // ~ (унарный)
	Shl,            // <<: счётчик маскируется по ширине левого операнда (31/63)
	Shr,            // >>: арифметический для знаковых, логический для беззнаковых

	// --- литералы коллекций (spec §3) ---
	ListAppend,     // нет операндов: [list, value] -> list с добавленным значением
	ListAppendRange, // нет операндов: [list, source] -> list, дополненный элементами list/array

	// --- паттерны списков (B4b1) ---
	ListLen,        // нет операндов: list/array -> i32 длина
	ListGet,        // нет операндов: [list/array, индекс i32] -> элемент

	// --- паттерны списков с rest (B4b2) ---
	ListSlice,      // нет операндов: [list/array, начало i32, конец i32] -> новый list<T> (копия)

	// --- литералы словарей (B3b2) ---
	DictSet,        // нет операндов: [dict, key, value] -> dict с записанной парой

	// --- встроенная индексация словаря (B5b1) ---
	DictGet,        // нет операндов: [dict, key] -> значение; нет ключа — паника

	// --- встроенная индексация str и срезы (B5b2) ---
	StrGet,         // нет операндов: [str, индекс i32] -> char; отрицательный индекс с конца, вне границ — паника
	Slice,          // inclusive (1 — конец включён): [list/array/str, начало i32, конец i32] -> новый list<T>/str

	// --- протоколы hash/compare (B6b1) ---
	ValueHash,      // нет операндов: снять значение, положить u64 — структурный хэш (unit — тип и поля)
	ValueCompare,   // нет операндов: [a, b] -> i32 (-1/0/1), полное структурное сравнение без паники

	// --- производные hash/compare по полям любого объекта (B8a) ---
	ValueHashFields,    // нет операндов: как ValueHash, но unit И type хэшируются по типу, счётчику и полям
	ValueCompareFields, // нет операндов: [a, b] -> i32; два объекта — тип и поля, иначе как ValueCompare

	// --- производный clone (B8c) ---
	CloneObject,    // нет операндов: снять объект, положить неглубокую копию (unit-поля скопированы)

	// --- доступ к элементу кортежа (spec §3) ---
	TupleGet,       // index: снять кортеж, положить элемент; индекс вне кортежа — паника

	// --- повтор строки/символа (spec §3: `3 * "Ha"`) ---
	Repeat,         // нет операндов: [значение, i64] -> повторённое значение (str/char)

	// --- ячейки для замыканий с изменяемым захватом ---
	NewCell,        // нет операндов: снять значение, положить ячейку
	LoadCell,       // нет операндов: снять ячейку, положить значение
	StoreCell,      // нет операндов: снять значение и ячейку, записать

	// --- обобщённый unit-объект (встроенный `result`: Ok<T>/Err<E>) ---
	NewObjectGeneric, // typeIdx, sigListIdx: объект типа с подставленными аргументами дженерика
}

/// <summary>Раскладка операндов и вспомогательные проверки для <see cref="OpCode"/>.</summary>
public static class OpCodeInfo
{
	/// <summary>Число операндов у опкода. Все операнды — int (32 бита).</summary>
	public static int OperandCount(OpCode op) => op switch
	{
		OpCode.PushConst or OpCode.PushI32 or OpCode.LoadLocal or OpCode.StoreLocal
			or OpCode.Jmp or OpCode.JmpIf or OpCode.JmpIfNot
			or OpCode.SaveSp or OpCode.RestoreSp
			or OpCode.NewObject or OpCode.LoadField or OpCode.StoreField
			or OpCode.LoadStatic or OpCode.StoreStatic
			or OpCode.NewTuple or OpCode.MakeFunc or OpCode.LoadCapture
			or OpCode.CallValue or OpCode.TypeOfSig or OpCode.IsType
			or OpCode.Slice or OpCode.TupleGet => 1,
		OpCode.PushI64 or OpCode.PushF64 or OpCode.Call
			or OpCode.NewList or OpCode.NewArray or OpCode.NewDict
			or OpCode.MakeClosure or OpCode.NewObjectGeneric => 2,
		_ => 0,
	};

	/// <summary>Размер инструкции в слотах кода (опкод + операнды).</summary>
	public static int InstructionSize(OpCode op) => 1 + OperandCount(op);

	public static bool IsJump(OpCode op) => op is OpCode.Jmp or OpCode.JmpIf or OpCode.JmpIfNot;

	/// <summary>Известен ли номер опкода (защита от мусора в .kbc).</summary>
	public static bool IsDefined(int value) => value >= 0 && value <= (int)OpCode.NewObjectGeneric;

	public static bool IsDefined(OpCode op) => IsDefined((int)op);
}
