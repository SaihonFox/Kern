# Внешние библиотеки Kern — частично реализованы

> `Kern.Lib.Db`, `Kern.Lib.Di`, `Kern.Lib.Backend` и `Kern.Lib.Ffi` реализованы и собираются в этом репозитории; `Ui` и `Transpile` пока остаются замыслом (`reference/libs`). Поиск библиотек — без `kern.toml` (см. `docs/BUILDER.md` §6).

Ядро (`src/`) содержит только язык, компилятор, VM и базовую стандартную библиотеку. Всё остальное — внешние библиотеки в `libs/`.

## Раскладка

    libs/
      Directory.Build.props   — вывод каждой библиотеки в libs/_out/<имя>/ (DLL + её зависимости)
      Kern.Libs.slnx          — решение для сборки всех библиотек
      Kern.Lib.Db/            — kern.db, kern.db.sqlite, kern.db.postgres, kern.db.mysql (реализовано)
      Kern.Lib.Ui/            — kern.ui (Avalonia; замысел)
      Kern.Lib.Ffi/           — kern.ffi (вызов C/C++ DLL; реализовано)
      Kern.Lib.Transpile/     — kern.transpile (Kern → C# / Kotlin; замысел)
      Kern.Lib.Di/            — kern.di (контейнер внедрения зависимостей; реализовано)
      Kern.Lib.Backend/       — kern.backend, kern.backend.server (HTTP, middleware, JWT, WS/SSE; реализовано)

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
- **SQL:** `exec(sql[, args])`, `query(sql[, args])`, `scalar(sql[, args])`, `begin/commit/rollback`, `last_id`, `close`; async — `exec_async`, `query_async`. Плейсхолдеры — `?` (для PostgreSQL внутренне заменяются на именованные `@p1, @p2…` — Npgsql переводит их в позиционные), ошибки SQL возвращаются как `Err(str)`. `query` возвращает `list<dict<str, object>>`; `bool` передаётся как boolean, а в ORM для PostgreSQL/MySQL создаётся колонка `BOOLEAN`.
- **ORM:** `orm.table<T>(db)` → `table<T>` (`create/drop/insert/update/delete/all/where/find/count`, `name`). Имя таблицы — snake_case + `s`; поле `id` целого типа — автоинкрементный ключ. Поддерживаются примитивные поля и `str`/`char`/`bool`/числа; остальные значения хранятся текстом (`to_str`), без JSON-разбора обратно.
- **Проверка:** `examples/15_databases` проходит `check` и `run` на SQLite, а с `KERN_PG_URL`/`KERN_MYSQL_URL` — на PostgreSQL/MySQL (exec/query/ORM/async/транзакции). `DbLiveTests` проверяет живые CRUD/boolean/rollback/`last_id` при заданных URL и молча пропускается без них. `e_sqlite3` ищется загрузчиком CLI в `runtimes/<rid>/native` рядом с DLL библиотеки.

## Kern.Lib.Ffi (kern.ffi)

Реализована (2026-11-02). Внешняя библиотека `libs/Kern.Lib.Ffi`, типы `native_lib` и `native_fn`.

- **Загрузка:** `native_lib c("msvcrt")` (Windows), `c("libc.so.6")` (Linux), `c("/usr/lib/libSystem.B.dylib")` (macOS), либо путь к файлу; ищутся имя как есть, `.dll`, `lib*.so`, `lib*.so.6`, `lib*.dylib`.
- **Функции:** `c.fn("strlen", "i64(str)")` → `native_fn`, `c.has("name")`, `c.close()`. `f.call(["abc"])` принимает `list`, возвращает значение; `f.name`, `f.signature`.
- **Типы:** `void bool i8 i16 i32 i64 u8 u16 u32 u64 f32 f64 ptr str`; `str` — UTF-8 (память аргумента освобождается после вызова), `ptr` — целое/nil; вызов cdecl через `calli`.
- **Ошибки:** библиотека/символ не найдены, неверная сигнатура, неверное число/тип аргументов — `KernPanic`.
- **Проверка:** `examples/20_ffi` (`check` + `run` на Windows: `strlen`, `abs`, `has`); `FfiTests` проверяют `strlen`, `abs`, `atof`, ошибки символа/сигнатуры/арности на текущей ОС.
- **Ограничения:** только cdecl; нет callbacks, struct, массивов и автоматического освобождения возвращённых строк/указателей (владелец — вызывающий C-код).

## Kern.Lib.Di (kern.di)

Реализована (2026-11-02). Внешняя библиотека `libs/Kern.Lib.Di`, тип `container` построен на текущих `TypeDef`/`KClassType`/`KObject` VM.

- **Регистрация:** `singleton<T>()`, `singleton<T>(fun -> T { … })`, `singleton<T>(fun(container k) -> T { … })`, то же для `transient` и `scoped`; последним аргументом можно дать имя (`"audit"`). Интерфейс → реализация: `singleton_as<I, Impl>()` (и `transient_as`, `scoped_as`). Готовый объект: `instance<T>(v)`.
- **Атрибуты:** `#[singleton]`, `#[transient]`, `#[scoped]` на типах + `c.scan()` (регистрирует тип под своим именем и под объявленными родителями); `#[inject]`, `#[inject("имя")]`, `#[inject(optional: true)]` на свойствах и полях. Это библиотечные атрибуты, поэтому компилятор по-прежнему выдаёт про них `KRN2307`-предупреждение.
- **Получение:** `get<T>()`, `get<T>("имя")`, `try_get<T>()` (nil), `has<T>()`, `create<T>()` (конструктор + `#[inject]`), `inject(obj)`, `scope()`, `dispose()` (вызывает `dispose()` у созданных объектов в обратном порядке; работает с `use`).
- **Конструктор:** помеченный `#[inject]`, иначе единственный/самый длинный; зависимости разрешаются по типу, цикл даёт panic с цепочкой.
- **Проверка:** `examples/26_di` проходит `check` и `run`: фабрики, named, scan, inject, scope, dispose, try_get.

## Kern.Lib.Backend (kern.backend, kern.backend.server)

Реализована (2026-11-02). Внешняя библиотека `libs/Kern.Lib.Backend` (зависит от `Kern.Lib.Di`) собирается в `libs/_out/Kern.Lib.Backend`. CLI разрешает зависимость `Kern.Lib.Di` из соседнего каталога `libs/_out/Kern.Lib.Di`.

- **Маршруты:** `app a(port[, host])`, `a.get/post/put/patch/delete/route(pattern, fun(ctx)->res)`, `a.group(prefix)` → `route_group`, `a.static_files(url, dir)`. Шаблоны: литералы, `{name}`, хвост `{*path}`; specificity выбирает самый точный маршрут; 404/405.
- **Middleware:** `a.use(fun(ctx c, chain n) -> res { n.next() })` и готовые `middleware.logger/recover/cors/bearer/basic/jwt_auth/jwt_optional/require_role/sessions/rate_limit`. Middleware группы применяется к её маршрутам; OPTIONS обрабатывает CORS.
- **Контекст:** `ctx` — `method/path/body/ip/user/roles/session/claims`, `param/query/header/cookie/has_role`, `json<T>()` → `result<T, str>`, `set/get`.
- **Ответ:** `res` — `text/html/json/json_status/send/redirect/no_content/not_found/error/bad_request/unauthorized/forbidden/file`, свойства `status/content_type/body`, `header(name, value)`, `cookie(name, value[, max_age])`.
- **Контроллеры:** `#[controller("/api")] type ... { #[get("/{id}")] fun show(i64 id) -> ... }`, `#[auth]`, `#[roles("admin")]`; регистрация `a.controller<T>()` или `a.scan_controllers()`. Параметры-примитивы — из `{name}`/query, остальные — JSON-тело; `ctx`/`session`/`claims` внедряются; инстанс создаётся через `a.services(container)` (scope на запрос, `dispose` в конце).
- **Auth/realtime:** `jwt.sign/verify` (HS256), сессии на cookie `kern_sid`, `a.ws(pattern, fun(socket s, str text) {...})`, `a.sse(pattern, fun(sse s) {...})`, `a.ws_broadcast/sse_broadcast`, `a.stop()`.
- **Запуск:** `await a.serve()` (модуль `kern.backend.server.*`; `serve` — член `app`). Цикл блокирующий: HTTP-поток складывает запросы в очередь, VM обрабатывает их в своём потоке; `accept()`/`handle()` доступны для ручного цикла.
- **Проверка:** `examples/13_db_web` (SQLite + ORM + HTTP) и `examples/27_backend` (DI, контроллеры, JWT, middleware, SSE, WebSocket, `/stop`) проходят `check` и `run`. В `src/Kern.Tests/Features/Backend` — 25 тестов: unit (маршруты, JWT, сессии, ответы/файлы/cookie) и loopback-интеграция (маршруты/CORS/адаптация ответов, контроллеры+DI+роли+query/session/claims, basic/bearer/rate-limit, static files, WebSocket echo+broadcast, SSE hello+broadcast, ручной `accept`/`handle`, свободный `serve(a)`).
- **Ограничения:** сервер на `HttpListener` (Windows без admin для `localhost` проверен; на других ОС нужен префикс/права), сессии и rate limit — в памяти процесса, SSE-буферы не переживают перезапуск, контроллеры не поддерживают async-методы с собственным `await` в ответе (адаптер умеет дождаться `task`), статические файлы без кэширования/диапазонов.

## Версии NuGet (проверено 2026-10-15)

Версии пакетов — в `.csproj` (Db: Microsoft.Data.Sqlite 10.0.13, Npgsql 10.0.3, MySqlConnector 2.6.2; проверялись 2026-10-13..15). Avalonia — `Version="*"` (последняя при restore; по данным раунда 13 это 12.1.x, API 12.x компиляцией не проверялся); Kern.Tests: Microsoft.NET.Test.Sdk 18.*, xunit 2.*.

## План

`.kbc`-библиотеки через `use` (нужны сигнатуры типов в .kbc); UI «с нуля» (desktop/mobile/web с роутингом) как новые бэкенды поверх общей модели виджетов; Avalonia остаётся одним из бэкендов.
