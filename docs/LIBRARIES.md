# Внешние библиотеки Kern — частично реализованы

> `Kern.Lib.Db`, `Kern.Lib.Di`, `Kern.Lib.Backend`, `Kern.Lib.Ffi` и `Kern.Lib.Transpile` реализованы и собираются в этом репозитории; `Ui` пока остаётся замыслом (`reference/libs`). Поиск библиотек — без `kern.toml` (см. `docs/BUILDER.md` §6).

Ядро (`src/`) содержит только язык, компилятор, VM и базовую стандартную библиотеку. Всё остальное — внешние библиотеки в `libs/`.

## Раскладка

    libs/
      Directory.Build.props   — вывод каждой библиотеки в libs/_out/<имя>/ (DLL + её зависимости)
      Kern.Libs.slnx          — решение для сборки всех библиотек
      Kern.Lib.Db/            — kern.db, kern.db.sqlite, kern.db.postgres, kern.db.mysql (реализовано)
      Kern.Lib.Ui/            — kern.ui (Avalonia; замысел)
      Kern.Lib.Ffi/           — kern.ffi (вызов C/C++ DLL; реализовано)
      Kern.Lib.Transpile/     — kern.transpile (Kern → C# / Kotlin; реализовано)
      Kern.Lib.Di/            — kern.di (контейнер внедрения зависимостей; реализовано)
      Kern.Lib.Backend/       — kern.backend, kern.backend.server (HTTP, middleware, JWT, WS/SSE; реализовано)

Сборка: `dotnet build libs/Kern.Libs.slnx`. Ядро: `dotnet build Kern.slnx` — без пакетов СУБД и Avalonia.

**Важно:** пока библиотеки не собраны, `use kern.db…`, `use kern.ui`, `use kern.ffi` дают ошибку KRN1101 «модуль не найден» (в сообщении есть подсказка). После сборки DLL лежат в `libs/_out/<имя>/`, и kernc находит их сам (если запущен из репозитория; иначе — `KERN_LIBS` или `libs` рядом с kernc).

## Встроенные модули 2026-11-02: `kern.security`, `kern.text`, `kern.parse`, `kern.num`

Это нативные модули в `src/Kern.Runtime` (не внешние `libs/`), доступны без сборки DLL.

- **`kern.security`**: SHA-3 (`sha3_256`, `sha3_512`), BLAKE2b (`blake2b(text[, size])`,
  `blake2b_keyed(key, text[, size])`, ключ и дайджест 1..64 байта), `hmac_sha512`,
  `aes_gcm_encrypt/decrypt`
  (ключ 16/24/32 байта, nonce+tag в Base64), `chacha20_encrypt/decrypt` (32-байтовый ключ),
  `rsa_generate(2048..4096)`, `rsa_public`, `rsa_sign`, `rsa_verify`, `jwt_sign`/`jwt_verify`
  (HS256), `hash_password(pwd, iterations)`/`verify_password` (PBKDF2-SHA256),
  `random_bytes`, `hex_encode/decode`, `constant_time_eq`, тип `secret<str>`
  (`secret<str>("…")`, `reveal()`, маскирующий `to_str()`, `len`). Базовые SHA-2/HMAC-SHA-256
  остаются в `kern.crypto`, TOTP — в `kern.auth` (имена там уже заняты).
  *Ограничения:* нет BLAKE3, Ed25519, argon2/bcrypt (нет в BCL без внешних пакетов);
  `secret` не сериализуется автоматически — раскрытие только через `reveal()`.
- **`kern.text`** (расширения): `levenshtein`, `fuzzy_contains`, `transliterate`, `slug`,
  `normalize(text, "NFC"|"NFD"|"NFKC"|"NFKD")`, `graphemes`, `escape_html`, `markdown_to_html`,
  `template_render(template, dict)` с `{{key}}`, `{{#if key}}…{{else}}…{{/if}}`,
  `{{#each items}}…{{/each}}` (`{{this}}`, `{{@index}}`, `{{else}}` для пустой коллекции) и
  `{{& key}}` без HTML-экранирования; `template_render_with(template, values, partials)` добавляет
  `{{> partial}}`. Значения по умолчанию экранируются как HTML. `to_utf8`/`from_utf8`.
  Члены `str` (2026-11-03): `s.graphemes()`, `s.grapheme_len`, `s.grapheme_at(i)`, `s.normalize(form)`; `len`/индексы остаются в UTF-16, `normalize` — no-op при `InvariantGlobalization=true`.
  Эффективная сборка строк — тип `string_builder`: `string_builder([capacity])`, `append(...)`,
  `append_line(...)`, `len`, `is_empty`, `clear()`, `to_str()`.
  *Ограничения:* Markdown — подмножество без таблиц и вложенных списков; шаблонизатор не
  вычисляет выражения в тегах (только имена полей, `this` и `@index`).
- **`kern.parse`**: `parse_ident`, `parse_integer`, `parse_number`, `parse_quoted`,
  `is_ident`, `parse_error(text, offset)` (строка:колонка + подчёркивание).
  *Ограничения:* комбинаторы (`~>`, `many`, `sep_by`, PEG) ещё не реализованы.
- **`kern.num`**: `mean`, `median`, `variance([sample])`, `stdev`, `correlation`,
  `regression -> [slope, intercept]`, `dot_product`, `transpose`, `matmul`, `identity`, `solve`,
  `det`, `inverse`, `matrix_rank([eps])`, `qr -> [Q, R]`, `eigen_symmetric([iterations]) -> [values, vectors]`,
  `interpolate`, `integrate(f, a, b[, steps])` (Симпсон), `root(f, a, b[, iterations])`
  (бисекция), `fft` (re/im парами), тип `complex` (`complex(re, im)`, `re`, `im`, `abs`,
  `conjugate`, операторы `+ - * /`, `to_str`). *Ограничения:* собственные `rational`/векторные
  типы и разреженные матрицы пока не добавлены; `eigen_symmetric` — только симметричные
  матрицы (Якоби), `solve`/`det`/`inverse` — Гаусс с частичным выбором ведущего элемента.

## `use dotnet.X[...]` (способ C)

```kern
use dotnet.System.IO[File, Path]
use dotnet.System[Math]

File.WriteAllText("out.txt", "ok")
str text = File.ReadAllText("out.txt")
str name = Path.GetFileNameWithoutExtension("data/report.md")
f64 absolute = Math.Abs(-3.5)
```

Статические и экземплярные методы/свойства BCL становятся членами типа с **именами как
в C#** (`PascalCase`): `File.ReadAllText`, `Math.Abs`, `b.Append`, `b.Length`,
`b.Capacity = 64`. Статические члены импортируются и у обычных классов (`String.Join`,
`Int32.Parse`), не только у static-классов. Конструкторы доступны как targetless-фабрики
(`StringBuilder()`). Работает и в `exe`/`dist`: в `.kbc` хранится `dotnet:<полное имя>` и
`NativeResolver` находит метод рефлексией.

```kern
use dotnet.System.Text[StringBuilder]
use dotnet.System[String]

StringBuilder b = StringBuilder()
b.Append("a")
b.Capacity = 64
array<str> parts = <str>["a", "b"]
println(String.Join("-", parts))
println(b.Length)
```

*Открытые generic-типы и продвинутые сигнатуры:* `use dotnet.System.Collections.Generic[List, Dictionary]`
даёт `List<i32>`, `Dictionary<str, i32>`; `params T[]` принимает и развёрнутый список, и
массив; `ref`/`out` отображаются на `*T` (`Int32.TryParse("42", *value)`); делегаты
`Action`/`Func`/`Predicate`/`Comparison`/`Converter` принимают Kern-функции
(`xs.ForEach(fun(i32 x) ...)`). *Ограничения:* generic-методы BCL и члены с не
импортированными типами пропускаются; делегаты работают только Kern → CLR и не в
NativeAOT; открытый generic-тип закрывается примитивами, Kern-коллекциями или типами из
того же `use dotnet`. Тип должен быть в загруженной сборке (явно пробуются
`System.Runtime`, `System.Console`, `System.IO.FileSystem`).



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

Транспилятор работает над типизированным деревом компилятора (`BoundModule`), поэтому получает уже разрешённые перегрузки, типы и привязки паттернов. Цели — `csharp` и `kotlin`. Первый этап — **ядро языка**: функции, `type`/`unit`, `match` с паттернами и guard, `loop`/`while`, generics, `result`, лямбды, `use`-освобождение (try/finally), свойства. Нативные библиотеки (`kern.db`, `kern.ui`…) не переносятся — попадают в заголовок файла как «нет правила подстановки».

    Kern.Lib.Transpile/
      Api/KernTranspiler.cs      — Kern-тип `transpiler` (text / file / project / map_script)
      Core/                      — Lowerer (общий обход: Dest-ориентированный вывод операторов, hoisting), CodeWriter, Naming, ProgramLoader, Transpiler
      Mapping/                   — IMappings, BuiltinMappings (print/str/list/dict), ScriptMappings (Kern-скрипт)
      Targets/CSharp, Targets/Kotlin — синтаксис языков + объявления + runtime-хелперы (KResult, диапазоны, in, срезы)

Подстановки пользователя — Kern-файл с функциями `map_type(target, kern_type, args) -> str?`, `map_call(target, owner, name, receiver, args) -> str?`, `map_member(target, owner, name, receiver) -> str?` (`nil` — «правила нет»). Порядок: скрипт → встроенная таблица → имя по соглашению (C# PascalCase, Kotlin camelCase). Пример: `examples/25_transpile` (`use kern.transpile.*`; сгенерированный C# проверяется компиляцией в тестах, Kotlin — только текстом, без `kotlinc`).

Известные ограничения: множественное наследование (берётся первый родитель), данные в enum, union (→ `object`/`Any`), кортежи длиннее 3 в Kotlin, сложные guard в `match`, порядок побочных эффектов внутри выражений if/match в позиции значения (они выносятся во временные переменные), std-функции, написанные на Kern (не переносятся).

## Kern.Lib.Db (kern.db, kern.db.sqlite, kern.db.postgres, kern.db.mysql)

Реализована (2026-11-02). Внешняя библиотека `libs/Kern.Lib.Db` (пакеты Microsoft.Data.Sqlite, Npgsql, MySqlConnector) собирается в `libs/_out/Kern.Lib.Db`.

- **Базы:** `sqlite db(path)` (`:memory:`), `postgres db(url)` / `postgres db(host, port, database, user, password)`, `mysql db(...)`; также `postgres.connect(url)` / `mysql.connect(url)` → `result<..., str>`. URL: `postgres://user:pass@host:5432/db`, `mysql://user:pass@host:3306/db`.
- **SQL:** `exec(sql[, args])`, `query(sql[, args])`, `scalar(sql[, args])`, `begin/commit/rollback`, `last_id`, `close`; async — `exec_async`, `query_async`. Плейсхолдеры — `?` (для PostgreSQL внутренне заменяются на именованные `@p1, @p2…` — Npgsql переводит их в позиционные), ошибки SQL возвращаются как `Err(str)`. `query` возвращает `list<dict<str, object>>`; `bool` передаётся как boolean, а в ORM для PostgreSQL/MySQL создаётся колонка `BOOLEAN`.
- **ORM:** `orm.table<T>(db)` → `table<T>` (`create/drop/migrate/insert/update/delete/all/where/find/count`, `name`). Имя таблицы — snake_case + `s`; поле `id` целого типа — автоинкрементный ключ. `migrate()` создаёт отсутствующую таблицу и добавляет недостающие колонки, не удаляя данные. Не-примитивные поля (`unit`/`type`, `list`, `dict`) хранятся как JSON и разбираются обратно. Связи: `orm.belongs_to<TParent,TChild>(parent, child, "field")` → `relation<TParent,TChild>.find(parent_item) -> TChild?` (внешний ключ в parent), `orm.has_many<TParent,TChild>(parent, child, "field")` → `.all(parent_item) -> list<TChild>` (внешний ключ в child); `field`/`many` — свойства связи. Миграции без версий/истории; каскадов и eager-loading нет.
- **Версионные миграции:** `use kern.db[migrator, migration]`; `migration m("202601011200", "create_users", fun (database d) { d.exec("…") })`, `migrator mg(db)`, `mg.add(m)`, `mg.up() -> result<list<str>, str>` (неприменённые версии по возрастанию, каждая в транзакции), `mg.pending()`, `mg.applied()`, свойство `mg.table` (по умолчанию `kern_migrations`). История — `kern_migrations(version, name, applied_at)`; `table<T>.migrate()` остаётся синхронизацией схемы без истории.
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
- **Атрибуты:** `#[singleton]`, `#[transient]`, `#[scoped]` на типах + `c.scan()` (регистрирует тип под своим именем и под объявленными родителями); `#[inject]`, `#[inject("имя")]`, `#[inject(optional: true)]` на свойствах и полях. Компилятор знает эти имена (без `KRN2307`) и проверяет их цели и аргументы (`KRN2101`); смысл атрибутов по-прежнему реализует `kern.di`.
- **Получение:** `get<T>()`, `get<T>("имя")`, `try_get<T>()` (nil), `has<T>()`, `create<T>()` (конструктор + `#[inject]`), `inject(obj)`, `scope()`, `dispose()` (вызывает `dispose()` у созданных объектов в обратном порядке; работает с `use`).
- **Конструктор:** помеченный `#[inject]`, иначе единственный/самый длинный; зависимости разрешаются по типу, цикл даёт panic с цепочкой.
- **Проверка:** `examples/26_di` проходит `check` и `run`: фабрики, named, scan, inject, scope, dispose, try_get.

## Kern.Lib.Backend (kern.backend, kern.backend.server)

Реализована (2026-11-02). Внешняя библиотека `libs/Kern.Lib.Backend` (зависит от `Kern.Lib.Di`) собирается в `libs/_out/Kern.Lib.Backend`. CLI разрешает зависимость `Kern.Lib.Di` из соседнего каталога `libs/_out/Kern.Lib.Di`.

- **Маршруты:** `app a(port[, host])`, `a.get/post/put/patch/delete/route(pattern, fun(ctx)->res)`, `a.group(prefix)` → `route_group`, `a.static_files(url, dir)`. Шаблоны: литералы, `{name}`, хвост `{*path}`; specificity выбирает самый точный маршрут; 404/405; обычный HTTP на WebSocket-путь получает `426` с заголовком `Upgrade: websocket`.
- **Middleware:** `a.use(fun(ctx c, chain n) -> res { n.next() })` и готовые `middleware.logger/recover/cors/bearer/basic/jwt_auth/jwt_optional/require_role/sessions/rate_limit`. `sessions("file.json")` сохраняет сессии между перезапусками, `sessions()` держит их в памяти. Middleware группы применяется к её маршрутам; OPTIONS обрабатывает CORS.
- **Контекст:** `ctx` — `method/path/body/ip/user/roles/session/claims`, `param/query/header/cookie/has_role`, `json<T>()` → `result<T, str>`, `set/get`.
- **Ответ:** `res` — `text/html/json/json_status/send/redirect/no_content/not_found/error/bad_request/unauthorized/forbidden/file`, свойства `status/content_type/body`, `header(name, value)`, `cookie(name, value[, max_age])`. Потоковая отдача: `res.stream("text/plain", fun (res_writer w) { w.write("…") })`, у `res_writer` — `write/write_line/flush/close/cancelled`; `ctx.cancelled` взводится при обрыве клиента.
- **Контроллеры:** `#[controller("/api")] type ... { #[get("/{id}")] fun show(i64 id) -> ... }`, `#[auth]`, `#[roles("admin")]`; регистрация `a.controller<T>()` или `a.scan_controllers()`. Параметры-примитивы — из `{name}`/query, остальные — JSON-тело; `ctx`/`session`/`claims` внедряются; инстанс создаётся через `a.services(container)` (scope на запрос, `dispose` в конце).
- **Auth/realtime:** `jwt.sign/verify` (HS256), сессии на cookie `kern_sid`, `a.ws(pattern, fun(socket s, str text) {...})`, `a.sse(pattern, fun(sse s) {...})`, `a.ws_broadcast/sse_broadcast`, `a.stop()`. SSE-события получают `id`; при подключении с `Last-Event-ID` сервер проигрывает пропущенные события из окна 256.
- **OpenAPI:** `a.openapi("/openapi.json"[, title, version])` отдаёт OpenAPI 3.1 JSON, построенный по зарегистрированным HTTP-маршрутам; `a.openapi_document([title, version]) -> str` возвращает тот же документ строкой; `a.openapi_ui("/docs"[, json_path])` — самодостаточная HTML-страница со списком операций (без CDN). Path-параметры `{name}`/`{*rest}` — обязательные `string`; `operationId` строится из метода и пути; WebSocket/SSE в документ не попадают.
- **Запуск:** `await a.serve()` (модуль `kern.backend.server.*`; `serve` — член `app`). Цикл блокирующий: HTTP-поток складывает запросы в очередь, VM обрабатывает их в своём потоке; `accept()`/`handle()` доступны для ручного цикла. Фоновый pump перезапускает `HttpListener` после транзиентной ошибки HTTP.sys; сервер завершается только по `a.stop()`/`stop()`.
- **Проверка:** `examples/13_db_web` (SQLite + ORM + HTTP) и `examples/27_backend` (DI, контроллеры, JWT, middleware, SSE, WebSocket, `/stop`) проходят `check` и `run`. В `src/Kern.Tests/Features/Backend` — 26 тестов: unit (маршруты, JWT, сессии/файл, ответы/файлы/cookie) и loopback-интеграция (маршруты/CORS/адаптация ответов, контроллеры+DI+роли+query/session/claims, basic/bearer/rate-limit, static files с ETag/Range, WebSocket echo+broadcast, SSE hello+broadcast+replay, ручной `accept`/`handle`, свободный `serve(a)`).
- **Ограничения:** сервер на `HttpListener` (Windows без admin для `localhost` проверен; на других ОС нужен префикс/права), rate limit — в памяти процесса, SSE-буфер живёт в памяти (не переживает перезапуск); потоковая отдача есть через `res.stream`/`res_writer`, отмена — через `ctx.cancelled` и `res_writer.cancelled` (HTTP.sys не даёт отдельный cancellation-token).

## kern.random (нативный модуль)

`src/Kern.Runtime/Random/RandomApi.cs`, `[KernModule("kern.random")]`. Внутренний std:

- **Криптостойкие:** `random.i32([min,] max)`, `random.i64([min,] max)`, `random.f64()` (`[0,1)`), `random.bool()`, `random.bytes(n) -> list<u8>`, `random.hex(n) -> str`, `random.choice<T>(list<T>)`, `random.shuffle<T>(list<T>)` (копия), `random.sample<T>(list<T>, n)`.
- **Детерминированные:** `random.rng(seed) -> rng`; `r.next_i32([min,] max)`, `r.next_i64()`, `r.next_f64()`, `r.next_bool()`, `r.choice<T>`, `r.shuffle<T>`, `r.sample<T>`. Один и тот же seed даёт одну и ту же последовательность (xorshift64).
- `bytes`/`hex` ограничены 1 МиБ; пустой список для `choice`, `n < 0`/`n > len` для `sample`, `min >= max` — panic по-русски.
- **Проверка:** `src/Kern.Tests/Features/RandomTests.cs`.

## kern.ansi / kern.ini / kern.toml / kern.net и часовые пояса

Нативные внутренние модули (2026-11-02):

- **`kern.ansi`** — `red/green/yellow/blue/magenta/cyan/white/gray/bold/dim/italic/underline`, `reset`, `strip`, `color256`, `rgb`, свойство `supports` (учитывает перенаправление вывода и `NO_COLOR`).
- **`kern.ini`** — `ini.parse(text) -> result<dict<str, dict<str, str>>, str>` (секции, глобальные ключи в `""`, комментарии), `ini.to_str(data)`.
- **`kern.toml`** — `toml.parse(text) -> result<dict<str, object>, str>` и `toml.to_str(value)`; таблицы, массивы (включая многострочные), inline-таблицы `{ … }`, массивы таблиц `[[a]]`, hex/oct/bin и `_` в числах, `inf`/`nan`; даты и время читаются как ISO-строки.
- **`kern.net`** — `hostname`, `resolve`, `ip_of`, `local_addresses`, `is_reachable(host, port, timeout_ms)`; без сокет-клиента (HTTP — `kern.http`).
- **`kern.time` time zones** — `time.zone(name)`, `time.zones()`, `time_zone.name/offset_minutes`, `datetime.now_in(tz)`, `dt.in_zone(tz)`, `dt.zone_name`; `datetime` хранит UTC и отображается в поясе.
- Проверка: `StdModulesExtraTests`.

## kern.auth (нативный модуль)

`src/Kern.Runtime/Auth/AuthApi.cs`, `[KernModule("kern.auth")]`. Одноразовые коды и токены:

- **TOTP/HOTP:** `auth.secret([nbytes=32]) -> str` (Base32), `auth.totp(secret[, unix_time])`, `auth.totp_verify(secret, code[, window=1])`, `auth.totp_url(issuer, account, secret[, digits=6, period=30])`, `auth.hotp(secret, counter)`, `auth.hotp_verify(secret, code, counter[, window=0])`. HMAC-SHA1 по RFC 6238/4226; неверный код — `false`.
- **Токены:** `auth.token([nbytes=32]) -> str` (Base64URL без паддинга), `auth.token_hex([nbytes=16])`, `auth.constant_time_eq(a, b)`.
- **Проверка:** `src/Kern.Tests/Features/AuthTests.cs` (известные векторы RFC, окно, токены).

## Модули инструментов (`kern.log`, `kern.cli`, `kern.config`, `kern.process`)

Нативные модули в `src/Kern.Runtime` (не внешние библиотеки), контракт — spec §16 «Библиотеки».

- **`kern.log`** (`src/Kern.Runtime/Logging`): глобальный логгер процесса. `log.level` (свойство), `set_level`, `to_stderr`, `to_stdout`, `to_file(path) -> result<bool, str>`, `close`, `log(level, msg)`, `trace/debug/info/warn/error(msg)`. Формат: ISO-8601 UTC + уровень + текст; уровень по умолчанию `info`.
- **`kern.cli`** (`src/Kern.Runtime/Cli`): `args()`, `parser(name, description)` → `cli_parser` с `flag/option/parse/usage`, результат — `cli_values` с `flag/option/positional/has/help_requested`. Поддержка `--name value`, `--name=value`, `-n value`, `-nvalue`, `--`.
- **`kern.config`** (`src/Kern.Runtime/Config`): `load(path)`, `from_str(text, format)`, `empty()` → `config`; чтение `get/get_or/get_i64/get_f64/get_bool/get_list`, `has/keys/set/merge/to_str`. Форматы: JSON, YAML, `.env`, `key=value`; вложенные ключи через `.`.
- **`kern.process`** (`src/Kern.Runtime/Processes`): `run/run_in/shell` → `result<process_output, str>`, `spawn` → `process_handle` (`pid/wait/kill/try_wait/stdout_read_line/stderr_read_line/write_stdin/close_stdin`), `which(name)`.
- **Проверка:** `src/Kern.Tests/Features/StdToolModulesTests.cs`.

## Версии NuGet (проверено 2026-10-15)

Версии пакетов — в `.csproj` (Db: Microsoft.Data.Sqlite 10.0.13, Npgsql 10.0.3, MySqlConnector 2.6.2; проверялись 2026-10-13..15). Avalonia — `Version="*"` (последняя при restore; по данным раунда 13 это 12.1.x, API 12.x компиляцией не проверялся); Kern.Tests: Microsoft.NET.Test.Sdk 18.*, xunit 2.*.

## План

`.kbc`-библиотеки через `use` (нужны сигнатуры типов в .kbc); UI «с нуля» (desktop/mobile/web с роутингом) как новые бэкенды поверх общей модели виджетов; Avalonia остаётся одним из бэкендов.
