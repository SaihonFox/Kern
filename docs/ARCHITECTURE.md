# Kern — архитектура (целевая, для реализации агентами)

> Целевая архитектура новой реализации с нуля. Отличия от старой попытки (`reference/`): нет `kern.toml` (всё в `build.kern`, `docs/BUILDER.md`);
> потоки с общей памятью (не изолированные VM); `never`-тип; async-комбинаторы и отмена входят в базовый этап; UI и внешние библиотеки (ffi, transpile — второй этап; db, di, backend реализованы в `libs/`, см. `docs/LIBRARIES.md`).
> Структура проектов первого этапа — `docs/CONVENTIONS.md`.

Решения заказчика (Saihon): байткод + VM; reified generics; GUI на Avalonia (внешняя библиотека); выход `kernc exe` = `.kbc` + VM-хост;
несколько csproj в одном `Kern.slnx`; неймспейсы = пути папок; привязка std к C# через атрибуты на C#-классах
(способ A) + прямой доступ к .NET (`use dotnet.X[...]`, способ C); автосвойства `i32 val { get; set }`;
библиотеки: web-сервер, DB/ORM (SQLite, PostgreSQL, MySQL — внешняя библиотека), xml/yaml/csv, тест-фреймворк + mocking, Refit-подобный HTTP-клиент, regex, рефлексия, время, потоки.
НИКАКИХ `#[extern]` на каждом члене и НИКАКИХ строковых реестров вида `"len.get"`.

## 1. Проекты и зависимости (src/)

| Проект | Namespace-корень | Зависит от | Содержимое |
|---|---|---|---|
| Kern.Core | `Kern.Core` | — | SourceText (смещение → строка/столбец, `Kern.Core.Text`), TextSpan, Diagnostic(Bag), KernException |
| Kern.Bytecode | `Kern.Bytecode` | Core | OpCode, KbcModule и все *Def, TypeSig (подписи типов), KbcWriter/KbcReader |
| Kern.Vm | `Kern.Vm.*` | Core, Bytecode | Values, Types (reified, адаптеры нативных типов), Execution (интерпретатор, ModuleLoader), Async (планировщик), Reflection (TypeInfo/MethodInfo…, DynamicTypes — типы на лету), INativeResolver |
| Kern.Binding | `Kern.Binding.*` | Vm, Bytecode | Атрибуты `[KernType]` и др., обёртки `KList<T>`…, NativeCatalog (reflection → символы для компилятора), NativeResolver (вызов), маршалинг |
| Kern.Runtime | `Kern.Runtime.<Домен>` | Binding | Реализации std по доменам: Build, Collections (+Containers: collection/set/queue/stack/observable), Compression, Core, Crypto, Env, Events, IO, Json, Math, Net, Numerics, Random, Reflection, Regex, Serialization.{Xml,Yaml,Csv}, Testing, Text, Threading, Time, Uuid, Web |
| Kern.Compiler | `Kern.Compiler.*` | Core, Bytecode, Binding | Lexing, Syntax (AST), Parsing, Semantics, CodeGen, Optimization (ConstantFolder, PeepholeOptimizer), Formatting (`kernc fmt`) |
| Kern.Std | — | — | `.kern` исходники std (сейчас `collections.kern` — протоколы `iter`/`async_iter`, и `server.kern`), EmbeddedResource `std/**` |
| libs/* (**второй этап**) | `Kern.Lib.*` | Runtime | внешние библиотеки: Db, Di, Backend реализованы; Ui, Ffi, Transpile — замысел |
| Kern.Cli | `Kern.Cli` | Runtime, Compiler, Vm, Binding | `kernc`: run, check, build, exe, dll, dis, fmt, repl, clean, tasks, do, help, version (без `test` в первом этапе); соглашения проекта и сценарии `build.kern` (`docs/BUILDER.md`); поиск внешних библиотек (`LibraryLoader`) |
| Kern.Tests | `Kern.Tests.*` | все | C#-тесты + прогон `*.kern` тестов |

Правило папок: НЕ класть много файлов в одну папку; один домен = одна подпапка = один namespace.

## 2. Конвейер

`source → Lexer (UTF-8, без BOM-проблем) → Parser → AST → DeclarationCollector → Binder/Checker (символы, типы, дженерики, перегрузки,
свойства, smart-cast, flow-analysis; рефлексия времени компиляции `name_of<T>()` и др.) → BoundProgram → CodeGen (+ Optimizer) → KbcModule (.kbc) → VM (Interpreter)`.
Имена исходников в .kbc хранятся относительно папки проекта.

Консоль всегда UTF-8: `Console.OutputEncoding = new UTF8Encoding(false)` и `InputEncoding` — в Kern.Cli и в exe-хосте
ДО первой записи (баг «?» вместо русских букв). Файлы читаются как UTF-8.

## 3. Модель значений (Kern.Vm.Values)

`readonly struct KValue { ValueKind Kind; long Bits; object? Ref; }`
Числа точного типа: Kind ∈ {I8,I16,I32,I64,U8,U16,U32,U64,F16,F32,F64}; арифметика проверяет Kind и делает wrap/ошибку
по правилам C#. `Char`, `Bool`, `Str(string)`, `Nil`, `None`, `Void`. Ссылки: `KObject` (type и unit; value-семантика unit — копирование опкодом `CopyUnit`),
`KListObject`, `KArrayObject`, `KDictObject`, `KTupleObject`, функции-значения/замыкания (`Callables.cs`, захваты в `KCell`),
`KTask` (async), `KTypeObject` (значение `typeof`), `KNativeBox` (любой .NET-объект из `use dotnet`).

## 4. Reified generics (Kern.Vm.Types)

`KType` — интернированные рантайм-типы: `KPrimType`, `KClassType(def, KType[] args)`, `KFuncType`, `KTupleType`,
`KNullableType`, `KUnionType`, `KBuiltinGenericType` (list/array/dict/result/task), `KNativeType`. `list<i32>` ≠ `list<str>`.
Фрейм хранит `KType[] TypeArgs` (аргументы типа класса `this` + аргументы метода). Опкод `Call*` несёт индекс
`TypeSigList` в пуле — перед вызовом VM подставляет текущие `T` и интернирует `KType`.
`typeof<T>()` → опкод `TypeOfSig sigIdx`; `typeof(str name)` → обычный вызов. Это ПЕРЕГРУЗКИ: разрешение учитывает
число generic-аргументов (arity), число и типы параметров, возвращаемый тип НЕ участвует (как в C#), но участвует
при выводе для делегатов. Идентичность перегрузки = `Name + GenericArity + ParamTypeSigs`.

## 5. Символы, члены, перегрузки

Каждый член — отдельный `FunctionDef`/`PropertyDef` со своим id; никакого слияния по имени.
Свойство = `PropertyDef { Name, Type, Getter?, Setter?, Init?, IsAuto, BackingField? }`; геттер/сеттер — отдельные
`FunctionDef` с флагами `IsGetter/IsSetter/IsInit`. Автосвойство `i32 val { get; set }` порождает backing-field.
Операторы — `FunctionDef` с `OpSymbol`. Расширения — `FunctionDef` с `ExtensionOf: TypeSig`.
Разрешение перегрузки — как в C#: применимость → лучший кандидат (identity > implicit widening > …),
именованные аргументы и значения по умолчанию участвуют; неоднозначность = ошибка с перечислением кандидатов.

## 6. Привязка к C# (Kern.Binding) — способ A + C

```csharp
namespace Kern.Runtime.Text;

[KernType("str")]                                  // C#-класс «приклеивается» к Kern-типу str
public static class StrApi
{
    [KernProperty] public static int Len(string self) => self.Length;       // свойство str.len (get)
    [KernMember]   public static string Replace(string self, string a, string b) => ...;  // fun replace(str,str)->str
    [KernMember]   public static string Replace(string self, char a, char b)   => ...;    // отдельная перегрузка
    [KernMember]   public static KList<R> Map<T, R>(KList<T> self, KFunc<T, R> f) => ...; // настоящий generic
}

[KernType("Counter")]
public sealed class Counter
{
    [KernProperty] public int Value { get; set; }       // get+set из C#-свойства; init → { get; init; }
    [KernCtor]     public Counter(int start = 0) { }
    [KernMember]   public int Next() => ++Value;        // метод экземпляра
    [KernStatic]   public static Counter Zero() => new();// #fun
}
```
Правила:
1. Имя Kern = snake_case от C#-имени, либо явно `[KernName("x")]`. Kern-тип по умолчанию = `[KernType]` имя.
2. Статический класс + первый параметр `self` ⇒ члены встроенного/расширяемого типа (`str`, `i32`, `list<T>`…).
3. Нестатический класс ⇒ экземпляр хранится в `KNativeBox`; `[KernCtor]` ⇒ конструктор.
4. Типы подписей C# → Kern: `int→i32`, `long→i64`, `byte→u8`, `sbyte→i8`, `uint→u32`, `float→f32`, `double→f64`,
   `Half→f16`, `bool`, `char`, `string→str`, `void`, `T?`/nullable → `T?`, `KList<T>→list<T>`, `KArray<T>→array<T>`,
   `KDict<K,V>→dict<K,V>`, `KTuple<..>→(..)`, `KFunc<A..,R>→fun(A..)->R`, `KAction<A..>→fun(A..)`, `KResult<T,E>→result<T,E>`,
   `KTask<T>`/`Task<T>`→`task<T>`, `KType→type`, `KObject→object`, `KValue→object`, enums → Kern enums, любой другой
   класс с `[KernType]` → он сам. Generic-параметры C# = generic-параметры Kern. Ограничения: `where T : IKernNum`→`num` и т.д.
5. Обёртки `KList<T>` — struct-обёртки над VM-объектами; T — фантом (в рантайме метод инстанцируется с `T=KValue`),
   элементы всегда читаются как `KValue`. Reified аргументы: последний параметр `[KernTypeArgs] KType[] typeArgs`.
6. `async`: метод возвращает `Task`/`Task<T>`/`ValueTask` ⇒ Kern `task<T>`; `await` в VM подписывается на задачу.
7. Параметры по умолчанию C# → defaults Kern. Имена параметров → именованные аргументы Kern (snake_case).
8. Исключения C# внутри нативов → `panic` Kern с сообщением, либо `result.Err` если метод возвращает `KResult`.
9. `NativeCatalog.Scan(Assembly)` строит символы для компилятора (без запуска). `NativeResolver` создаёт делегаты
   (скомпилированные `Expression`-деревья, не `MethodInfo.Invoke`) для VM.
10. Способ C (интероп): `use dotnet.System.IO[File, Path]` — компилятор спрашивает `NativeCatalog` (раздел dotnet), который рефлектит
    любой загруженный .NET-тип и применяет те же правила маппинга; имена → snake_case; `File.read_all_text(p)`.
11. Наследование нативных типов берётся из C#: `class KernSet<T> : KernCollection<T>` ⇒ `set<T>` наследует `collection<T>` (база тоже `[KernType]`). Kern-типы от нативных не наследуются. Встроенные list/array/str/dict/range получают родителя `collection<…>` правилом компилятора (`NativeType.CollectionParentOf`), а в нативный код подаются через адаптеры (`[KernAdapter]`, `IValueAdapter`).
12. Внешние библиотеки (`libs/`) — сборки `Kern.Lib.*.dll` с теми же атрибутами; `LibraryLoader` загружает их до компиляции и до запуска VM. `NativeResolver` ищет члены по `NativeKey` среди ЗАГРУЖЕННЫХ сборок, поэтому `Pipeline.CreateVm` явно загружает `Kern.Runtime` (иначе .kbc без компиляции не найдёт нативы).
13. `.kern` в std НЕ дублируют сигнатуры нативных типов. Они `use` их: `use kern.text[str]`. Атрибут `#[native]`
    нужен только когда Kern-файл хочет явно дополнить нативный тип: один раз на тип, не на член.

## 7. Async

`async fun f() -> T` компилируется в корутину (фрейм сохраняется в `KTask`). `await e` — опкод `Await`: если задача не завершена, фрейм
паркуется, планировщик (`Kern.Vm.Async.Scheduler`) выбирает следующий. IO-нативы (http, файлы, sleep, БД) возвращают .NET `Task`, завершение
будит планировщик (одиночный цикл событий). Доступно в языке: `async`/`await`, `async main`, `delay(ms)`, `async loop` по `async_iter<T>`
(`next() -> task<T?>`), `http`/`http_client`, `web_server.serve()`, async-запросы БД, `worker.wait()`, `channel.recv_async()`.
Базовый этап включает комбинаторы (`tasks.all/any/race/settled/select/timeout/delay/run/retry`), `cancel_token`, `pipe<T>`, `parallel.*`; потоки — **с общей памятью** (`kern.threading`, `Interpreter.Fork()`), `thread.run(f)` → `task<R>`. Задачи завершаются только в потоке своей VM (внешнее завершение — `Scheduler.BeginExternal/EndExternal`).

## 8. Байткод

Стековая машина; код функции — `int[]` (opcode + операнды). Пулы: strings, typeSigs, constants, functions, types, fields.
См. `Kern.Bytecode/OpCode.cs`. `.kbc` = бинарный формат с magic `KBC2` и версией (`KbcWriter`/`KbcReader`). Запись `kernc build` — по умолчанию в `<проект>/out/<имя>.kbc`.
`kernc exe`: копирует хост (файлы рядом с kernc, `kernc.exe` переименовывается в имя модуля) и кладёт рядом `program.kbc`; хост при старте запускает его. По умолчанию `<проект>/dist/<имя>`.
`kernc dll`: тот же .kbc, но без точки входа + интерфейсные заглушки (`kern.stub`) для подключения через `use` (`t.kbc(...)` в `build.kern`; спецификация — «Раунд 25»).

## 9. Оптимизации (Kern.Compiler.Optimization)

Свёртка констант; удаление мёртвого кода/недостижимых веток; пропагация констант; именованные → позиционные аргументы (частично). Реально есть: `ConstantFolder` (-O1) и `PeepholeOptimizer` (-O2); инлайнинг и удаление неиспользуемых функций не сделаны. Уровни `-O0/-O1/-O2` (по умолчанию -O1).

## 10. GUI — ВТОРОЙ ЭТАП (в первом проходе не делается; старый замысел для справки)

Avalonia. Модель — один тип `widget` с фабриками `ui.label/button/input/checkbox/slider/progress/list/column/row/window`, свойствами
(`text`, `value`, `enabled`, `visible`…) и событиями `on_click/on_change/on_close`; `ui.run(window)` запускает цикл. Декларативный Compose-стиль
(`state<T>`, recomposition), роутинг и мобильный/web-бэкенды — планируются. Хост `Kern.Lib.Ui.Hosting.GuiHost` строит Avalonia-контролы лениво;
события возвращаются в VM через планировщик.

## 11. Тесты и документация

`#[test]`, `assert*`, mocking (`mock.create<T>()`) — библиотека `kern.test` входит в этап 1; `kernc test` запускает помеченные функции (реализовано 2026-11-01). Рефлексия: `kern.reflection` (рантайм) и `name_of<T>()` и др. (компиляция). kern.md — спецификация языка (обновляется каждым релизом).
`docs/history/IMPLEMENTATION_GUIDE_old.md` — инструкция для другого ИИ. Любой выпуск проекта сопровождается обновлённым kern.md.
