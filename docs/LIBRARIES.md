# Внешние библиотеки Kern — частично реализованы

> `Kern.Lib.Db` и `Kern.Lib.Di` реализованы и собираются в этом репозитории; `Ui`, `Ffi`, `Transpile` и `Backend` пока остаются замыслом (`reference/libs`). Поиск библиотек — без `kern.toml` (см. `docs/BUILDER.md` §6).

Ядро (`src/`) содержит только язык, компилятор, VM и базовую стандартную библиотеку. Всё остальное — внешние библиотеки в `libs/`.

## Раскладка

    libs/
      Directory.Build.props   — вывод каждой библиотеки в libs/_out/<имя>/ (DLL + её зависимости)
      Kern.Libs.slnx          — решение для сборки всех библиотек
      Kern.Lib.Db/            — kern.db, kern.db.sqlite, kern.db.postgres, kern.db.mysql (реализовано)
      Kern.Lib.Ui/            — kern.ui (Avalonia; замысел)
      Kern.Lib.Ffi/           — kern.ffi (вызов C/C++ DLL; замысел)
      Kern.Lib.Transpile/     — kern.transpile (Kern → C# / Kotlin; замысел)
      Kern.Lib.Di/            — kern.di (контейнер внедрения зависимостей; реализовано)

Сборка: `dotnet build libs/Kern.Libs.slnx`. Ядро: `dotnet build Kern.slnx` — без пакетов СУБД и Avalonia.

**Важно:** пока библиотеки не собраны, `use kern.db…`, `use kern.ui`, `use kern.ffi` дают ошибку KRN1101 «модуль не найден» (в сообщении есть подсказка). После сборки DLL лежат в `libs/_out/<имя>/`, и kernc находит их сам (если запущен из репозитория; иначе — `KERN_LIBS` или `libs` рядом с kernc).

## Где kernc ищет библиотеки

1. переменная `KERN_LIBS` (список путей через `;` на Windows, `:` на Linux/macOS);
2. `libs` рядом с `kernc`;
3. `libs/_out` выше по дереву от kernc (для запуска из репозитория);
4. `~/.kern/libs`;
5. `libs` в папке проекта;
6. пути из `build.kern`: `w.lib_path("../my-libs")`, `t.lib_path("C:/kern/Kern.Lib.Chart.dll")`.

Корень — папка (DLL лежат в ней или в подпапках первого уровня) либо сам `.dll`. Подхватываются файлы `Kern.Lib.*.dll`; зависимости (например `Npgsql.dll`) ищутся рядом с ними.

## Своя библиотека на C#

    // Kern.Lib.Chart.dll
    [assembly: KernModule("kern.chart", "Kern.Lib.Chart")]
    namespace Kern.Lib.Chart;
    [KernType("chart")]
    public sealed class KernChart { [KernCtor] public KernChart() {} [KernMember] public string Render() => "…"; }

Проект ссылается на `Kern.Runtime` с `<Private>false</Private>` (его DLL не копируется). В Kern: `use kern.chart[chart]`.

## C/C++ (FFI)

`use kern.ffi[native_lib]`, затем `native_lib c("msvcrt")`, `var f = c.fn("strlen", "i64(str)")`, `f.call(["abc"])`. См. `examples/20_ffi`.

## Kern.Lib.Transpile (kern.transpile)

Транспилятор работает над типизированным деревом компилятора (`BoundProgram`), поэтому получает уже разрешённые перегрузки, типы и привязки паттернов. Цели — `csharp` и `kotlin`. Первый этап — **ядро языка**: функции, `type`/`unit`, `match` с паттернами и guard, `loop`/`while`, generics, `result`, лямбды, `use`-освобождение (try/finally), свойства. Нативные библиотеки (`kern.db`, `kern.ui`…) не переносятся — попадают в заголовок файла как «нет правила подстановки».

    Kern.Lib.Transpile/
      Api/KernTranspiler.cs      — Kern-тип `transpiler` (text / file / project / map_script)
      Core/                      — Lowerer (общий обход: Dest-ориентированный вывод операторов, hoisting), CodeWriter, Naming, ProgramLoader, Transpiler
      Mapping/                   — IMappings, BuiltinMappings (print/str/list/dict), ScriptMappings (Kern-скрипт)
      Targets/CSharp, Targets/Kotlin — синтаксис языков + объявления + runtime-хелперы (KResult, диапазоны, in, срезы)

Подстановки пользователя — Kern-файл с функциями `map_type(target, kern_type, args) -> str?`, `map_call(target, owner, name, receiver, args) -> str?`, `map_member(target, owner, name, receiver) -> str?` (`nil` — «правила нет»). Порядок: скрипт → встроенная таблица → имя по соглашению (C# PascalCase, Kotlin camelCase). Пример: `examples/25_transpile`.

Известные ограничения: множественное наследование (берётся первый родитель), данные в enum, union (→ `object`/`Any`), кортежи длиннее 3 в Kotlin, сложные guard в `match`, порядок побочных эффектов внутри выражений if/match в позиции значения (они выносятся во временные переменные), std-функции, написанные на Kern (не переносятся).

## Kern.Lib.Db (kern.db, kern.db.sqlite, kern.db.postgres, kern.db.mysql)

Реализована (2026-11-02). Внешняя библиотека `libs/Kern.Lib.Db` (пакеты Microsoft.Data.Sqlite, Npgsql, MySqlConnector) собирается в `libs/_out/Kern.Lib.Db`.

- **Базы:** `sqlite db(path)` (`:memory:`), `postgres db(url)` / `postgres db(host, port, database, user, password)`, `mysql db(...)`; также `postgres.connect(url)` / `mysql.connect(url)` → `result<..., str>`. URL: `postgres://user:pass@host:5432/db`, `mysql://user:pass@host:3306/db`.
- **SQL:** `exec(sql[, args])`, `query(sql[, args])`, `scalar(sql[, args])`, `begin/commit/rollback`, `last_id`, `close`; async — `exec_async`, `query_async`. Плейсхолдеры — `?` (для PostgreSQL компилятор SQL заменяет на `$1, $2, …`), ошибки SQL возвращаются как `Err(str)`. `query` возвращает `list<dict<str, object>>`.
- **ORM:** `orm.table<T>(db)` → `table<T>` (`create/drop/insert/update/delete/all/where/find/count`, `name`). Имя таблицы — snake_case + `s`; поле `id` целого типа — автоинкрементный ключ. Поддерживаются примитивные поля и `str`/`char`/`bool`/числа; остальные значения хранятся текстом (`to_str`), без JSON-разбора обратно.
- **Проверка:** `examples/15_databases` проходит `check` и `run` на SQLite (create/insert/ORM where). PostgreSQL/MySQL API компилируются, но для end-to-end нужны живые серверы. `e_sqlite3` ищется загрузчиком CLI в `runtimes/<rid>/native` рядом с DLL библиотеки.

## Kern.Lib.Di (kern.di)

Реализована (2026-11-02). Внешняя библиотека `libs/Kern.Lib.Di`, тип `container` построен на текущих `TypeDef`/`KClassType`/`KObject` VM.

- **Регистрация:** `singleton<T>()`, `singleton<T>(fun -> T { … })`, `singleton<T>(fun(container k) -> T { … })`, то же для `transient` и `scoped`; последним аргументом можно дать имя (`"audit"`). Интерфейс → реализация: `singleton_as<I, Impl>()` (и `transient_as`, `scoped_as`). Готовый объект: `instance<T>(v)`.
- **Атрибуты:** `#[singleton]`, `#[transient]`, `#[scoped]` на типах + `c.scan()` (регистрирует тип под своим именем и под объявленными родителями); `#[inject]`, `#[inject("имя")]`, `#[inject(optional: true)]` на свойствах и полях. Это библиотечные атрибуты, поэтому компилятор по-прежнему выдаёт про них `KRN2307`-предупреждение.
- **Получение:** `get<T>()`, `get<T>("имя")`, `try_get<T>()` (nil), `has<T>()`, `create<T>()` (конструктор + `#[inject]`), `inject(obj)`, `scope()`, `dispose()` (вызывает `dispose()` у созданных объектов в обратном порядке; работает с `use`).
- **Конструктор:** помеченный `#[inject]`, иначе единственный/самый длинный; зависимости разрешаются по типу, цикл даёт panic с цепочкой.
- **Проверка:** `examples/26_di` проходит `check` и `run`: фабрики, named, scan, inject, scope, dispose, try_get.

## Kern.Lib.Backend (kern.backend)
Backend-фреймворк: роутер (`app`, `route_group`), middleware, атрибутные контроллеры, JWT/сессии/роли, WebSocket и SSE. Зависит от `Kern.Lib.Di` (контроллеры создаются через `container`). Типы: `app`, `ctx`, `res`, `chain`, `middleware`, `jwt`, `claims`, `session`, `socket`, `sse`, `work`. Std-модуль `kern.backend.server` даёт `app.serve()`. Подробности — kern.md, «Раунд 21».

## Версии NuGet (проверено 2026-10-15)

Версии пакетов — в `.csproj` (Db: Microsoft.Data.Sqlite 10.0.13, Npgsql 10.0.3, MySqlConnector 2.6.2; проверялись 2026-10-13..15). Avalonia — `Version="*"` (последняя при restore; по данным раунда 13 это 12.1.x, API 12.x компиляцией не проверялся); Kern.Tests: Microsoft.NET.Test.Sdk 18.*, xunit 2.*.

## План

`.kbc`-библиотеки через `use` (нужны сигнатуры типов в .kbc); UI «с нуля» (desktop/mobile/web с роутингом) как новые бэкенды поверх общей модели виджетов; Avalonia остаётся одним из бэкендов.
