> ИСТОРИЯ: заметки о старой (ни разу не собиравшейся) реализации в `reference/`. Не руководство к действию: полезно для поиска ловушек и идей. Актуальные правила — `AGENTS.md` и `docs/`.

# Руководство по реализации Kern (для другого ИИ/разработчика)

Статус: ничего из этого репозитория ещё не компилировалось (в среде нет `dotnet`). Первый шаг — `dotnet build Kern.slnx` и исправление ошибок компиляции.

## Конвейер и где что лежит
Lexer → Parser → AST (`Kern.Compiler/Syntax`) → Семантика ч.1 (`Semantics/Declare`: символы, импорты) → Семантика ч.2 (`Semantics/Binding`: типизированное дерево `Bound*`) →
Оптимизация (`Optimization`: сейчас `ConstantFolder`) → CodeGen (`CodeGen`: `CodeGenerator.Generate(BoundProgram) → KbcModule`) → VM (`Kern.Vm`).
Контракт опкодов — комментарий в начале `Kern.Vm/Execution/Interpreter.Loop.cs` и `Kern.Bytecode/OpCode.cs`.

## Текущее состояние и что осталось (актуально на раунд 18b)
Готово (написано, не компилировалось): весь конвейер до VM; `kernc run|check|build|exe|dll|dis|test|fmt|repl|clean|tasks|do|help|version`; оптимизатор -O1/-O2;
рантайм-библиотеки `Kern.Runtime/*` (см. docs/ARCHITECTURE.md §1); внешние библиотеки `libs/*` (БД, UI, FFI); рефлексия (рантайм + компиляция); события, наследование нативных типов,
иерархия коллекций; `use` + `dispose()`; do-while; структурный match.
Осталось (по приоритету):
1. **Сборка и починка ошибок** по всем проектам (`dotnet build Kern.slnx`, затем `dotnet build libs/Kern.Libs.slnx`, затем `dotnet test`).
2. Транспилятор Kern → другие языки (внешняя библиотека, пользовательские соответствия); DI-библиотека (оба варианта: регистрация типов/фабрик и `#[inject]` на свойствах); backend-библиотека; архитектурные примеры (mvvm, mvc, mvp, mvi, viper).
3. UI «с нуля» (desktop/mobile/web, роутинг, иерархия widget), `.kbc`-библиотеки через `use`, LSP, Gradle-подобная сборка вместо `kern.toml`, богаче async (`task.all/any`, отмена), `in/ref/out` (отложено заказчиком), generics для union (вопрос открыт).
4. Оптимизатор: инлайнинг, удаление неиспользуемых функций, точный `MaxStack`.

## Примеры (examples/)
Проекты 01..24 (см. examples/README.md). Они же служат приёмочными тестами парсера/binder'а. Предположения о std, которых нет в kern.md:
`str.to_i32() -> i32?`, `list.length`/`str.length`, `delay(ms) -> task<void>`, `println`, `list + list`, `IComparable<T>`, `pub` на верхнеуровневых объявлениях,
`ret` внутри `?:`, `await t1 + await t2`. Если реализация решит иначе — поправить пример, а не наоборот; спорное вынести в вопрос заказчику.

## Правила сборки (ловушки компилятора C#)
- В `Kern.Runtime.*` есть пространство имён `Kern.Runtime.Math`, поэтому обращение к BCL — только `System.Math.X`.
- В `Kern.Compiler` конфликтуют `Syntax.{TypeKind,MemberFlags,FuncType}` и `Bytecode.{TypeKind,MemberFlags}`/`Semantics.Types.FuncType` — использовать `using Alias = ...`.

## Состояние: сквозной конвейер (добавлено)
- `Kern.Binding/Catalog/NativeCatalog.cs` — собирает нативные модули из сборок (`[KernType]`, статические классы с `[KernModule]`), dotnet-типы для `use dotnet.X[...]`.
  Адаптер для компилятора: `Kern.Compiler/Semantics/Project/CatalogNativeSource.cs`.
- Prelude-модуль называется `kern.core` (`ModuleMap.cs`, `ModuleMapIndex.DefaultModule`): `Core/CoreApi.cs` (print/println/eprintln/panic/delay/exit), `IO/ConsoleApi.cs` (readln).
- `Kern.Cli`: `kernc run|check|build|exe|dis|test` (`Program.cs`, `Commands/CliCommands.cs`, `Infrastructure/Pipeline.cs`, `ConsoleSetup.cs` — UTF-8).
  `exe` копирует хост kernc и кладёт рядом `program.kbc`; при наличии `program.kbc` рядом с хостом kernc сразу запускает его.
- `dll` — .kbc без точки входа (не .NET-обёртка); `repl` и `fmt` написаны в раунде 6.

## Состояние: рантайм-библиотеки (добавлено)
Готово (не компилировалось): `Collections/{ListApi,DictApi,ArrayApi,RangeApi,ResultApi}`, `Env`, `Random`, `Time`, `IO/{FileApi(file,path,dir),ConsoleApi}`, `Json`, `Testing/AssertApi`.
Std на Kern: `Kern.Std/std/kern/collections.kern` (только протоколы `iter<T>`/`async_iter<T>`) и `server.kern`. set/queue/stack — нативные generic-типы в `Kern.Runtime/Collections/Containers` (раунд 12c: Kern-версии падали на `list.len` и переписаны на C#).
`list + list` — конкатенация (Binder.Operators + Interpreter.Arith).
Все перечисленные далее библиотеки (regex, crypto, compression, threading, http, xml/yaml/csv, web, mocking, refit-клиент, БД, UI, FFI, repl/fmt, -O2, Kern.Tests) написаны в раундах 5–14. Образец для новых — `ListApi.cs`.
Известные риски, которые нужно проверить при первой сборке: правила префикса имён (SnakeCase) для `[KernStatic]`, `KernOperator("[]")`/`"in"` для нативных типов не используются — индексация и `in` обрабатываются в VM/Binder для встроенных коллекций.

## Решения заказчика (раунд 3) и их статус
- enum = union из unit → ничего нового в парсере; предположение примера 03: паттерн `Red _` для unit без полей (проверить парсер паттернов).
- интерфейсов нет; `#[interface]` добавлен в встроенные атрибуты (`AttributeBinder.BuiltIn`), семантики не имеет.
- `pub` по умолчанию для верхнего уровня (уже так: `Access.Default` видим, скрывает только `Priv`).
- `expr!` для result УДАЛЁН по решению заказчика: ошибки обрабатываются через `match` (`Ok(v)`/`Err(e)`) и `ret Err(...)`. `!` остаётся только как non-null assertion для `T?`.
- enum = union из unit; в `match` допустимы Rust-подобные ветки: `Red => …` (голое имя типа = проверка типа, Binder.Patterns) и `StreetAddress(city, street, house) => …` (деструктуризация по первичному конструктору).
- `async loop` реализован (Parser.Stmts/Exprs → Binder.Control → FunctionCompiler.Control.EmitAsyncLoop); VM-изменений не требует (Await + JmpIfNil). Не проверено компилятором.

## Раунд 4 (реализовано, не проверено компилятором)
- Парсер: `Parser.Stmts` — multi-declare (`_pendingStmts`), swap (`ParseMultiAssign`), локальные функции (`ParseLocalFunction` → `VarDeclExpr` с `FuncType` и лямбдой, `HoistLocalFunctions`), `Parser.Members` — `=>`-свойство, лексер `FatArrow`. Variadic `..` в параметрах — ошибка парсера.
- Семантика: `MemberDeclarer` — void по умолчанию; `BindLocalDeclaration` — ранняя декларация локальной функции (рекурсия); `FunctionCompiler.Statements` — ячейка до замыкания для рекурсивной лямбды; `BindLoop` — `FindStreamBase` (`iter`/`async_iter`), `LoopKind.Stream`, `EmitAsyncLoop` обслуживает и синхронный stream.
- Модули: `ProjectLoader.Load` — ленивый отбор std по `use`; `ImportResolver`/`Binder.Names` — свойства и константы нативных модулей; `NativeResolver.BuildField` — const-поля; `NativeScanner` — имя из `[KernStatic]` для статических свойств.
- Сборка: `Kern.Runtime/Build` (модуль `kern.build`: workspace/target), `Kern.Cli/Build` (BuildScript, BuildRunner), `Commands/WorkspaceCommand`, `HelpText`.
- while/бесконечный — раунд 7; do-while — раунд 15.

## Раунд 5: библиотеки (src/Kern.Runtime)
Regex/, Crypto/, Uuid/, Serialization/{Csv,Xml,Yaml}/, Net/HttpApi, Web/WebApi (+ std/kern/server.kern), Data/Sqlite (NuGet Microsoft.Data.Sqlite), Data/Orm (рефлексия по KClassType). Не реализовано: dotnet-хелперы, Refit-подобный клиент и моки (нужен dynamic-proxy API), Avalonia GUI (threading и compress — см. раунд 6).


## Раунд 6: потоки, сжатие, оптимизатор, команды
- `src/Kern.Runtime/Threading/`: `ThreadApi` (`thread`, `worker<R>`: `Interpreter.Fork()` — общая память, поток 16 МБ; см. раунд 24), `SyncApi` (`channel<T>`, `mutex`+`lock_guard`, `amutex`, `rw_lock`, `atomic`, `semaphore`), `Concurrent` (`conc_list/dict/queue`, `shared<T>`), `VmExec`.
- `src/Kern.Runtime/Compression/CompressApi.cs`: бинарные данные — base64-строки (в языке нет byte-буферов).
- `src/Kern.Compiler/Optimization/PeepholeOptimizer.cs`: вызывается из `FunctionCompiler.Finish` при `-O2`. Декодирует код в список инструкций со ссылками-целями; проходы до неподвижной точки; кодирует заново и пересчитывает цели и `LineTable`. Функции, у которых цель перехода не попадает на границу инструкции, пропускаются. Правило: инструкцию-адресат перехода не сливают с предыдущей.
- `src/Kern.Compiler/Formatting/SourceFormatter.cs` + `Kern.Cli/Commands/{FmtCommand,ReplCommand}.cs`; `CliCommands.Dll`. Форматтер строится на токенах лексера, а не на AST: комментарии лексер отбрасывает, поэтому AST-перепечатка их бы потеряла.
- Не сделано: инлайнинг, удаление неиспользуемых функций модуля, загрузка `.kbc`-библиотеки через `use` (пока зависимости — исходниками через `build.kern`), точный `MaxStack`.

- `src/Kern.Tests` (xunit): `Harness/KernHarness` (компиляция и запуск сниппета с перехватом stdout), `Formatting/FormatterTests`, `Optimization/OptimizerTests` (вывод при -O0/-O1/-O2 одинаков), `Execution/RunTests` (основные конструкции, потоки, gzip), `Examples/ExamplesCompileTests` (каждый examples/*/kern.toml должен типизироваться без ошибок). Запуск: `dotnet test`. Тесты тоже ещё не прогонялись — ожидания могут потребовать правки.

## Раунд 7: while / бесконечный loop, stop/next в выражениях
- Синтаксис: `Syntax.WhileExpr(Condition?, Body)`; `Parser.ParseLoop`: `loop {` → бесконечный; иначе `Speculate(ParsePattern + At(in))` → обход (`LoopExpr`), провал → `ParseExpr` как условие. `async loop` разбирается по-старому.
- Семантика: `Binder.BindWhile` (`BoundWhile`): `Flow.EnterLoop(AssignedNames.In(w))` до условия, `ApplyCondition` для сужения в теле, состояние выхода = `whenFalse` + все `stop`; бесконечный цикл без `stop` оставляет `pre`.
- Кодоген: `EmitWhile`; во всех циклах `BeginLoopBody/EndLoopBody` выдают `SaveSp slot` в начале итерации (после привязки переменной), `EmitLoopJump` пишет `RestoreSp slot; Jmp`. `CodeBuilder.Overwrite` заменяет неиспользованный `SaveSp` на два `Nop`.
- VM: опкоды `SaveSp`/`RestoreSp` (в конце enum, по 1 операнду), `sp` абсолютный в массиве регистров фрейма.

## Раунд 8: БД
- (теперь `libs/Kern.Lib.Db/Common`) `Data/Common`: `IKernDb` (контракт для ORM), `SqlDialect` (кавычки, типы, автоключ, плейсхолдеры, SQL последнего id), `DbCore` (общая реализация на ADO.NET: команды, параметры, чтение строк, транзакции, async), `ConnectionStrings` (URL → строка драйвера). Сканер идёт по `DeclaredOnly`, а наследование нативных типов (раунд 17) строится по `[KernType]`-базам; драйверы `KernSqlite/KernPostgres/KernMysql` остались тонкими обёртками над `DbCore` с повторяющимися `[KernMember]`.
- `OrmApi.Table<T>` — по перегрузке на каждый драйвер (не склеиваются), общий `OrmTable<T>` работает с `IKernDb`.
- NuGet: Microsoft.Data.Sqlite 10.0.13, Npgsql 10.0.3, MySqlConnector 2.6.2 (проверено по nuget.org 2026-10-13). Avalonia в `libs/Kern.Lib.Ui` — 12.1.4 (GitHub: последний релиз 12.1.3, NuGet: 12.1.4; в 12.x возможны отличия от 11.x — см. их migration guide, когда начнёте писать `kern.ui`).

## Раунд 9: моки
- `src/Kern.Runtime/Testing/MockApi.cs` (`[KernType("mock")]`, модуль `kern.test`): `Create<T>` строит `KObject` типа T с `Native = KProxyState`, `Tag = MockState` (правила + журнал вызовов). Диспетчер ищет правило: сначала с точными аргументами, затем общее; позднее перекрывает раннее. Имя метода берётся из `vm.Module.Functions[fi].Name` (перегрузки различаются только аргументами правил).
- Хуки VM: `Interpreter.Loop.cs` (`CallVirt`, impl < 0) и `Interpreter.Calls.cs` (`CallMethod`). Не проверено компилятором; пример `examples/16_mocks`.
- Для Refit-подобного клиента использовать тот же `KProxyState`: handler строит HTTP-запрос по атрибутам абстрактного метода.

## Раунд 10: HTTP-клиенты
- `src/Kern.Runtime/Net/HttpClientApi.cs`: `HttpRaw` (чистый .NET-обмен), `HttpClientApi` (вариант A) и `Proxy<T>` (вариант B на `KProxyState`; метод/путь берутся из `Reflector.MethodOf(vm, fi).Attributes`). Разбор JSON выполняется в потоке планировщика через `vm.Scheduler.Post`, чтобы не трогать типы VM из пула потоков.
- `CodeGenerator.EmitAttributes` заполняет `TypeDef/FunctionDef.Attributes`. Не проверено компилятором.

## Проверка импортов (раунд 10)
- `python3 tools/audit_kern_imports.py` (из корня): сверяет `use kern.*[…]` в examples с экспортами рантайма (`[KernType]`/`[KernModule]` + `ModuleMap.cs`).
- `python3 tools/audit_cs_usings.py`: эвристика пропущенных C# `using` (имя типа проекта используется без `using` его namespace). Выдаёт шум на общих именах (Func, Call, Scope…) — смотреть глазами. Проверка ссылок между csproj: нарушений нет.
- Найдено и исправлено: `HttpClientApi.cs` без `using Kern.Bytecode` (PrimKind). `kern.server` — модуль на Kern (`std/kern/server.kern`), не C#.

## Раунд 11 (по результатам первой сборки/тестов у заказчика)
- Стиль: табы + CRLF во всех исходниках (`.editorconfig`, `.gitattributes`); `SourceFormatter` по умолчанию так и пишет. Продолжения определяет парсер: `Parser.SkipNewLines(bool continuation)` складывает номера строк в `_continuationLines` (`ParserFacade.ParseForFormatting`). Структурные переносы (после атрибутов, `{`/`else` с новой строки) продолжениями не считаются.
- `BoundStop/BoundNext` имеют `NeverType` (раньше `void` — ломало `c ? x : (stop)` и narrowing).
- `Parser.ExpectMemberName` — имена функций/членов могут быть ключевыми словами (`next`).
- `Parser.ParseCompilationUnit` больше не подставляет имя проекта как имя модуля (`ProjectLoader.DeclaredName` берёт только явный `project`).
- `TypeMapper.Map(..., member)`: голый generic-T не nullable; `HasExplicitNullable` читает [Nullable(2)] на параметре/возврате.
- `NativeBinder.BindProperty` создаёт `TypeParamSymbol` для generic-параметров метода-свойства; `MemberLookup.AddExtensions` сохраняет карту Unify как `MemberMap`.
- `Binder.BindArguments` даёт подсказку по всем кандидатам, подходящим по числу аргументов (если параметры совпадают по типу).
- `Binder.Assign/BindLocalDeclaration`: narrowing по типу до `Convert`; `FlowAnalysis.AssignLocal` сужает примитивы до `T`.
- `BindMatch`: `nilCovered` — после `nil =>` привязка получает тип без `?`.
- `BindDestructurePattern`: unit/класс с первичным конструктором раскладывается по `(…)`.

## Раунд 12
- Parser.Stmts: ветка match `=>`; `ParseLoop` — `MarkLoopTargets` (BindingPattern.Declares), `loop <Range> body` → `LoopExpr(WildcardPattern)`.
- Binder.Patterns: `BindLoopAssignTarget` (присваивание существующей переменной); `BoundBindingPattern.IsAssignment`; codegen `StoreBinding`.
- AssignedNames.AddLoopTargets — захват/цикл учитывают присваивание.
- MemberLookup: второй проход `Unify(loose)` для расширений с generic-получателем; трассировка `KERN_TRACE_MEMBERS`.
- Operators: `<=>` → сравнение с 0 для `> < >= <=`; DecimalApi/BigIntApi `Cmp`.
- Kern.Runtime/Time: KDuration, KDateTime (self-типы через KNativeBox), Stopwatch, Time.
- Kern.Cli: `--time`/`KERN_TIME`.
- `async_stream` → `async_iter`.
- 12b: MemberLookup — третий проход по «голове» типа (`list`/`dict` по имени и арности) для свойств-расширений (`len`); Binder.Match — `IsNilValue` сквозь BoundConversion; Conversions: `T?`→`object`; Binder.Operators.Prim разворачивает `T[num]`.

## Раунд 13: GUI (позже вынесено в libs/Kern.Lib.Ui)
- `Widgets/KernWidget.cs` (тип `widget`: модель + синхронизация с Avalonia-контролом), `Widgets/UiApi.cs` (статический `ui`), `Hosting/GuiHost.cs` (AppBuilder, `StartWithClassicDesktopLifetime`, таймеры, `Fire` с перехватом KernPanic), `KernGui.cs` (`[assembly: KernModule("kern.ui", "Kern.Lib.Ui")]`).
- Контролы строятся лениво в `ui.run` (после старта приложения); события подписаны через `PropertyChanged`/`Click` — API стабильно между версиями Avalonia. Если при сборке Avalonia 12 что-то переименовано — править только libs/Kern.Lib.Ui/Widgets/KernWidget.cs и Hosting/GuiHost.cs.

## Раунд 14: внешние библиотеки (подробно — docs/LIBRARIES.md)
- Ядро (`src/`) больше не содержит БД и UI: они в `libs/Kern.Lib.Db`, `libs/Kern.Lib.Ui`, `libs/Kern.Lib.Ffi` (отдельный `libs/Kern.Libs.slnx`, вывод — `libs/_out/<имя>/`). Kern.Cli и Kern.Tests на них не ссылаются, NuGet-пакеты СУБД/Avalonia в kernc не попадают.
- `Kern.Binding/Catalog/LibraryLoader.cs`: поиск `Kern.Lib.*.dll` в корнях (KERN_LIBS, libs рядом с kernc, libs/_out выше по дереву, ~/.kern/libs, libs проекта, `libs = [...]` в kern.toml); загрузка в контекст по умолчанию + Resolving-хук для зависимостей. Pipeline создаёт NativeCatalog = Kern.Runtime + найденные сборки; CreateVm догружает библиотеки перед исполнением.
- KernHarness грузит библиотеки из `libs/_out`; Kern.Tests ссылается на проекты библиотек с `ReferenceOutputAssembly=false` (собираются, но не копируются).
- `Kv` стал public (нужен внешним библиотекам).
- Не сделано: загрузка скомпилированных `.kbc`-библиотек через `use` (нужны сигнатуры типов в .kbc) — следующий раунд.

## Раунд 15–17b: синтаксис, события, наследование нативных типов
- Лексер: `TryStringPrefix`/`StrMode` (префиксы `x r v fx fr fv f`, `"""` raw); парсер: `ParseLoop` — do-while `loop { }: cond` (`WhileExpr.PostCondition`), охрана ветки через `:`; `use` как локальное объявление (`VarDeclExpr.IsUse`) → `BoundLocalDeclaration.Dispose`, кодоген `_disposables`/`EmitDisposals` на выходе из блока, `ret`, `stop`, `next`.
- Структурные паттерны: `Syntax.NamedPattern`, `Binder.ReorderNamedFields` (имена → порядок первичного конструктора).
- События: `Kern.Runtime/Events/KernEvents.cs` (`event<T>`, `signal`; `+`/`-` мутируют и возвращают событие).
- Наследование нативных типов: `NativeScanner.KernParentOf` → `NativeTypeSymbol.Parent`, `NativeCatalog.LinkParents`, `NativeType.BaseType`, `Conversions.BaseTypes`, `MemberLookup` (члены баз, дедупликация по сигнатуре). Встроенные list/array/str/dict/range: `NativeType.CollectionParentOf`; рантайм-адаптеры `Kern.Vm.Types.NativeAdapters`, `[KernAdapter]`, `KernCollectionAdapter`.

## Раунд 18–18b: рефлексия и исправления
- `Kern.Runtime/Reflection`: `TypeApi` (члены `type`), `ReflectApi` (статический `reflect`), `KernFieldInfo/PropertyInfo/MethodInfo/ParamInfo/AttributeInfo`, `KernTypeBuilder` (`reflect.define_type`). Типы на лету: `Kern.Vm/Reflection/DynamicTypes.cs` + `ModuleLoader.AddType` + `ModuleIndex.AddType`; `TypeInfo.CreateInstance` без конструкторов присваивает аргументы полям по порядку.
- Компиляция: `Kern.Compiler/Semantics/Binding/Expressions/Binder.Meta.cs` (`name_of<T>()`, `field_names<T>()`, …); вызывается из начала `BindCall`.
- `Pipeline.CreateVm` загружает `Kern.Runtime` явно; `Pipeline.MakePathsRelative` делает пути исходников относительными; вывод `build/dll/exe` — в папку проекта (`out/`, `dist/`).
- `ImportResolver.MissingLibraryHint`: подсказка при отсутствии модулей внешних библиотек. `x"…"` раскрывается в `kern.regex.regex(…)`.
- Блокирующие риски первой сборки: `fun(T)` → `KAction<T>` в `event +`, имена `Dispose/WriteLine/ReadLine` в snake_case, статический `NativeType.CollectionRoot` при параллельных тестах, `range` как `collection<i64>`, свойства с `[KernVm]` в `TypeApi`, возвращаемый `KType` в `[KernProperty]`.

## Раунд 19: транспилятор (libs/Kern.Lib.Transpile)
- Вход: `ProgramLoader` (тот же конвейер, что `Pipeline` в kernc, но без CodeGen) → `BoundProgram`. Выход: `Dictionary<path, text>`.
- `Core/Lowerer*.cs` — база: `Stmt(expr, Dest)` выводит операторы (Dest: Discard/Return/Assign/AddTo), `Value(expr)` возвращает строку-выражение и при необходимости выводит подготовительные операторы (hoisting if/match/loop/block во временные переменные; `Capture` проверяет, нужны ли операторы, чтобы выбрать тернарник). Паттерны → условие + привязки (`PatternCond`). Язык подключается через абстрактные хуки (`Lowerer.Hooks.cs`) и `EmitModule`/`EmitType` в `CSharpLowerer`/`KotlinLowerer`.
- Подстановки: `IMappings` → цепочка `ScriptMappings` (Kern-скрипт, исполняется в отдельном `Interpreter`, функции ищутся по имени через `CallFunctionIndex`) → `BuiltinMappings`.
- Риски (код ни разу не собирался): свойства `Owner`/`Name` у `NativeMember` для свободных функций (ключ таблицы — `"" + "." + имя`), порядок параметров extension-функций (ресивер берётся из `BoundCall.Receiver`), реальное устройство первичных конструкторов (`FieldInitializers` + `AutoInit`).

## Раунд 20: DI (libs/Kern.Lib.Di)
- `Container/KernContainer.cs` — единственный нативный тип `container`; реестр `Dictionary<Key(KType, name), Registration>` общий для корня и областей, кэши экземпляров привязаны к `Registration` (алиасы родителей делят один экземпляр), список disposables на область.
- Создание: `Build` → `ChooseConstructor` → `TypeInfo.CreateInstance` → `InjectMembers` (`#[inject]` на `PropertyInfo`/`FieldInfo`).
- Скрытые параметры `[KernVm]`/`[KernTypeArgs]` идут после видимых; generic-аргументы читаются через `Kv.Last(typeArgs)` (для `_as<I, Impl>` — последние два).

## Раунд 21: backend (libs/Kern.Lib.Backend)
- `Http/` — `KernCtx` (обёртка HttpListenerContext), `KernRes` (+ `Takeover` для передачи соединения WebSocket/SSE), `KernSession`, `KernClaims`.
- `Middleware/` — `KernChain` (`Layer(ctx, chain)`/`Terminal(ctx)`), `KernMiddleware` (готовые слои), `Jwt`/`KernJwt` (HS256), `SessionStore`.
- `Routing/` — `RoutePattern` (`{p}`, `{*rest}`, Specificity), `RouteEntry`, `KernGroup` (маршруты, `use`, `group`, `static_files`, `ws`, `sse`), `KernApp` (очередь `Channel<KernWork>`, `Accept/Handle/Stop`, диспетчер, отправка ответа), `Controllers.cs` (рефлексия: `TypesWithAttribute("controller")`, связывание параметров, адаптация результата).
- `Realtime/` — `KernSocket` (чтение в фоне → очередь), `KernSse`, `KernWork`.
- Di: добавлен публичный `KernContainer.CreateByType(vm, type)` для использования из других библиотек.
- Std: `std/kern/backend/server.kern` (`work_stream`, `app.serve`). `MissingLibraryHint` знает `kern.backend*`.
- Риски (не компилировалось): имена нативных членов в snake_case (`jwt_optional`, `scan_controllers`, `sse_broadcast`), наследование `app` от `route_group`, перегрузки `use`/`ws`, `KAction<..>?` как nullable-структура.

## Раунд 22: макросы
- `Kern.Compiler/Semantics/Project/ISourceTransform.cs` — хук `ProjectLoader` (3-й необязательный параметр конструктора) + `ProjectLoader.LoadFiles`; коды KRN1501–1505 в `Krn`.
- `Kern.Runtime/Macros` — модуль `kern.macro`: `macro_ctx`, `macro_target`, `macro_field`, `macro_method`, `macro_attr` (чистые данные, строит хост).
- `Kern.Cli/Macros/MacroEngine*` — стадия 1: файлы с `#[macro]` → `ProjectLoader.LoadFiles` → `SemanticAnalyzer` → `CodeGenerator` → `Pipeline.CreateVm`, поиск `Reflector.FunctionsWithAttribute(vm,"macro")`; стадия 2: токены `Lexer` для `имя!(…)` и `ParserFacade` для атрибутов на `TypeDecl`, правки текста по смещениям. `Pipeline.CreateLoader` подключает движок.
- Риски: `m.decl.fields.len` и другие выражения в примере проверены только чтением; `MethodInfo.Invoke(default, …)` для функции верхнего уровня; `Initialize()` для статических инициализаторов макро-модуля.

## Раунд 23: async и параллелизм
- `Kern.Binding/Wrappers`: `KTaskRef<T>`, `KTaskNullable<T>`; `TypeMapper.MapCore` → `task<T>`/`task<T?>`.
- `Kern.Runtime/Tasks` (модуль `kern.tasks`): `TasksApi` (комбинаторы поверх `KTask.All/Any/OnCompleted`), `TaskMembers` (`[KernType("task")]` — расширение builtin), `KernCancelToken` (обёртка `KCancellation`), `KernPipe<T>` (очереди на KTask, без блокировок — всё в потоке VM), `ParallelApi` (форкнутые VM на потоках, возврат через `Scheduler.EndExternal`), `KernPool`.
- Std: `std/kern/pipes.kern` (`pipe_stream<T>`).
- Риски: самый вероятный сбой — привязка `[KernType("task")]` как расширения builtin-типа и вывод T в `KList<KTaskRef<T>>`; при проблемах заменить на явные `[KernStatic]` без self.

## Раунд 24: потоки с общей памятью
- `Interpreter` получил закрытый конструктор-копию и `Fork()`: общие `Module/Natives/Info/_funcs/_isInstance/_sigCache`, `_initialized = true`; свой `Scheduler`. Общее: `Info.Statics`, `Info.Classes`, `TypeFactory` (уже под замком). Под замок взят `EnumValuesCache`; `AddType` (рефлексия) уже под `lock(info)`.
- Правило: `KTask`/`Scheduler` трогает только владелец-поток; рабочие возвращают результат через `Scheduler.BeginExternal/EndExternal`. `KFunc.Callable` исполняет в VM, создавшей вызов, — внутри нативов, вызываемых в рабочем потоке, это его форк.
- `ParallelApi.Drive`: общий счётчик `Interlocked.Increment`, рабочие берут индексы динамически; `Reduce` сворачивает долю каждого рабочего и объединяет частичные результаты в потоке-владельце.
- Замок-защитник — обычный `use` (`dispose()` у `lock_guard`); парсер и binder не менялись.
- Риски: гонки внутри интерпретатора, которых я не нашёл чтением (например, ленивые поля `Info`, которых нет в `grep ??=` по `Kern.Vm`, и кэш `_sigCache` — гонка безвредна, т.к. типы интернируются); при ошибках смотреть `Fork()` и `VmExec.Run`.

## Раунд 25: .kbc через use
- `Kern.Cli/Kbc`: `KbcStubs` (Build — вырезание тел по спанам AST: `FunDecl/ExtensionFunDecl/ExtensionPropDecl/ExtensionOpDecl/OpDecl/CtorDecl/Accessor.Body`; Read — атрибуты модуля `kern.stub` → `SourceText`), `KbcConfig` (kern.toml `kbc = [...]`, кэш по mtime), `KbcStubTransform` (цепочка после `MacroEngine`), `KbcLinker`.
- `Pipeline.CreateLoader` оборачивает трансформацию; `Compile`/`CompileSources` вызывают `Pipeline.Link` после `CodeGenerator.Generate`; `CompileSources` теперь создаёт загрузчик с `root`. `CliCommands.Dll` вызывает `Load → Analyze → Generate → Link` и дописывает `KbcStubs.Build(project)`.
- Линкер: ключ функции `owner|name|flags&KeyFlags|op|generics|paramSigs|ext|file(static-init)`; кандидаты в заглушки — функции с `SourceFile` на `<kbc>/<lib>/`, сгенерированные без файла у типов библиотеки и `<static-init>` модулей библиотеки. Таблица операндов — `Relocator.Code` (сверять с `OpCodeInfo`/`Interpreter.Loop` при добавлении опкодов: `CallNative [str, argc, list]`, `Call/CallVirt [func, argc, list]`, `NewObj/NewUnit [type, ctor, argc, list]`, `NewList/NewArray/NewDict/NewTuple [sig, n]`, `LdEnum [type, member]`, `LdStatic/StStatic [global]`; переходы абсолютные, длина кода не меняется).
- Риски: порядок/дубликаты ключей сгенерированных функций; `Types.TryAdd(Name)` — одноимённые типы; нет юнит-тестов (нужен dotnet).
