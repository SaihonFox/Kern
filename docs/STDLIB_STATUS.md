# STDLIB_STATUS — библиотеки и что они разблокируют

Карта на 2026-11-02: языковое ядро M1–M3 и этапы M6–M12 закрыты. Открыты M13 (package fetching) и часть M14: структуры данных, doc/CLI-инструменты, `kern.net`/`kern.ansi`/`kern.toml`/`kern.ini`, часовые пояса, UI.
Таблица помогает выбирать следующий срез по эффекту на `examples/`.

## Реализовано (частично)

| Модуль | Что есть | Ограничения |
|---|---|---|
| `kern.core` | `print`, `println`, `eprintln`, `panic`, `delay`, `exit`, `format`, `to_str`, `typeof`, `name_of` | — |
| `kern.io` | `streamwriter`/`streamreader` (`write`, `write_line`, `read_line`, `read_all`, `read_text`, `close`, `dispose`, `is_open`, `at_end`), `ConsoleModule` | — (файловые `file.*` — в строке ниже) |
| `kern.test` | `assert`, `assert_eq`, `assert_ne`, `assert_near`, `assert_nil`, `assert_not_nil`, `fail` | `fail` типизирован `void`, а не `never` |
| `kern.math` | базовые функции | — |
| `kern.collections` | нативные `list`/`array`/`dict` (включая `first/last/get/set/map/filter/keys/values/get_or`), члены `str`/`char`; generic-контейнеры `set<T>`, `queue<T>`, `stack<T>`, `observable<T>` (add/remove/clear/on_change); общий протокол `collection<T>` (len/is_empty/contains/to_list) с адаптерами и итерацией | — |
| `kern.text` | `len/upper/lower/trim*/contains/starts_with/ends_with/replace/repeat/split/lines/join/chars/from_chars/parse_i32/i64/f64/pad_left/right` | — |
| `kern.numerics` | `decimal` и `bigint`: фабрики из str/i64/f64, арифметика, сравнения, `to_str/to_f64/to_i64`, `abs/round/truncate/pow/compare` | `-0`/переполнение decimal — как в .NET |
| `str`/`char` (встроенные) | `len`, `upper`, `lower`, `trim*`, `contains`, `starts_with`, `ends_with`, `index_of`, `substring`, `replace`, `split`, `chars`, `lines`, `join`, `pad_left/right`, `repeat`, `to_str`, `to_i32/to_i64/to_f64/to_bool` | `str` пока не все члены из старых примеров |
| `kern.regex` | `regex(pattern[, flags])` → `regex` (panic по-русски), `try_new`, члены `is_match/find/find_all/match/find_match/replace/split`, свойства `pattern/options`; `match` → `regex_match?` (`text/start/end/len/groups/group`); `x"…"`/`fx"…"` lowering | `regex.try_new(…)` пока только `try_new(…)` |
| `kern.json` | `to_str`, `to_str_pretty`, `parse` → `dict<str, object>`/`list<object>`, `from_str<T>` (reified `[KernTypeArgs]`) | `Err` только при ошибке разбора |
| `kern.env` | `get`, `get_or`, `set`, свойства `os`, `cwd`, `home` | — |
| `file.*` / `kern.path` | чтение/запись/список/копирование/удаление (IO → `result<T, str>`); `join/dir_name/file_name/ext/normalize/is_absolute/absolute` | — |
| `kern.csv` / `kern.yaml` / `kern.xml` | CSV RFC 4180 (`parse`, `parse_dicts`, `to_str`); YAML-подмножество (`parse`, `to_str`); XML (`parse`, `new`, `xml_node.name/text/children/attr/child/find_all/add`) | YAML без якорей/тегов; XML — только элементы и атрибуты |
| `kern.crypto` / `kern.uuid` | sha1/sha256/sha512/md5, hmac_sha256, base64, random_hex, hash_password/verify_password; `uuid.new()` | — |
| `kern.time` | `duration` (parse/from_*/total_*/арифметика/сравнения/печать), `datetime` (of/now/поля/weekday/format/add_months/±/сравнения), `stopwatch` (запускается сразу), `time.sleep/measure/unix_*` | нативные параметры по умолчанию не поддерживаются: `datetime.of` требует все 6 аргументов |
| `kern.reflection` / `kern.macro` | `type` (`name/full_name/args/parents/fields/properties/methods/constructors/attributes`, `is_*`, `enum_names`, `get_*/get/set/create/is_instance/is_assignable_from`), `field_info/property_info/method_info/param_info/attribute_info`, `reflect.*`, `define_type`; `macro_ctx` + `#[macro]`/`#[macro("expr")]`/`#[derive]`/`name!(...)` | `define_type` с generics/родителями, но без методов/свойств; `attribute_info.instantiate()` есть; макросы не раскрываются в одиночном `CompileCore` |
| `kern.db.*` / `kern.db` | внешняя `libs/Kern.Lib.Db`: `sqlite`, `postgres`, `mysql` (`exec/query/scalar/begin/commit/rollback`, async, `last_id`, `close`), `orm.table<T>` (`create/drop/migrate/insert/update/delete/all/where/find/count`, `name`), JSON-поля, `belongs_to`/`has_many`, версионные `migration`/`migrator` | SQLite/PostgreSQL/MySQL проверены живыми CRUD/boolean/транзакциями (`DbLiveTests`); связи без каскадов/eager-loading |
| `kern.ffi` | внешняя `libs/Kern.Lib.Ffi`: `native_lib`/`native_fn`, загрузка `.dll/.so/.dylib`, `fn(symbol, "i32(i32, str)")`, `call([...])`, `has`, `close`; cdecl, типы void/bool/i8…u64/f32/f64/ptr/str | только cdecl; без callbacks/struct/массивов; возвращённые строки/указатели освобождает C-код |
| `kern.di` | внешняя `libs/Kern.Lib.Di`: `container` (`singleton/transient/scoped`, `*_as`, `instance`, `scan`, `get/try_get/has/create/inject/scope/dispose`, `count`) | фабрики требуют явного типа результата лямбды (`fun -> T`); атрибуты библиотеки теперь известны компилятору (без `KRN2307`) |
| `kern.backend` / `kern.backend.server` | внешняя `libs/Kern.Lib.Backend`: `app`/`route_group`/`ctx`/`res`/`res_writer`/`chain`/`middleware`/`jwt`/`session`/`claims`/`socket`/`sse`/`work`; маршруты, middleware, контроллеры (`#[controller]`, `#[get]`, `#[auth]`, `#[roles]`), DI-scope на запрос, JWT HS256, сессии (в памяти/файле), WebSocket, SSE с replay, static files с ETag/Range, `res.stream`, OpenAPI (`a.openapi`), `await a.serve()` | `kern.backend.server.*` — импорт модуля; `serve` объявлен членом `app`; rate limit/SSE-буфер в памяти; сервер на `HttpListener`; отмена потокового ответа кооперативная |
| `kern.transpile` | внешняя `libs/Kern.Lib.Transpile`: `transpiler("csharp"\|"kotlin")`, `map_script`, `text/file/project` → `result`; цели C#/Kotlin, `KResult`-runtime, подстановки `map_type/map_call/map_member` | сгенерированный C# проверяется сборкой (`TranspileTests`); Kotlin — только текстом (нет `kotlinc`); сложные конструкции упрощаются с предупреждением |
| `kern.log` / `kern.cli` / `kern.config` / `kern.process` | внутренние std-модули (2026-11-02): уровни/файл логов; парсер аргументов; JSON/YAML/`.env`/`key=value`; `run/run_in/shell/spawn/which` | — (`log.level = …` и `to_file -> result<bool, str>` поддержаны) |
| `kern.auth` / `kern.random` | внутренние std-модули (2026-11-02): TOTP/HOTP/токены; криптостойкие числа/байты и детерминированный `rng(seed)` | TOTP — HMAC-SHA1; `rng` — xorshift64 и хранит состояние (продолжает последовательность) |
| `kern.net` / `kern.ansi` | `net.hostname/resolve/ip_of/local_addresses/is_reachable`; ANSI-цвета/стили, `strip`, `supports` | сеть — DNS/TCP-проверка без сокет-клиента; ANSI не включает цвета при перенаправлении вывода |
| `kern.ini` / `kern.toml` | `ini.parse/to_str`; `toml.parse/to_str` (таблицы, массивы, скаляры) | TOML без дат/inline-таблиц/массивов таблиц |
| `kern.time` time zones | `time.zone/zones`, `datetime.now_in/in_zone`, `zone_name`, `tz.offset_minutes` | `datetime` хранит UTC, отображается в поясе |
| M14-структуры | `src/Kern.Runtime/Collections`: `deque`, `priority_queue`, `sorted_dict`, `sorted_set`, `bitset`, `ring_buffer`, `lazy`, `frozen_list/dict/set`, `span`; пример `32_data_structures` | сортировка поддерживает числа/bool/char/str/unit/`op compare`; `span` не проверяет stack-only |
| LSP | `src/Kern.Lsp` (`kern-lsp`, JSON-RPC over stdio): диагностика, completion, hover, definition, symbols, formatting | только каталог `Kern.Runtime` (внешние `Kern.Lib.*` не подгружаются); hover/definition синтаксические |

## Отсутствует (M13/M14 и UI)

- `kern.ui` (Avalonia, `libs/Kern.Lib.Ui` отсутствует; `examples/19_gui` не запускается).

- Инструменты: `kernc wasm` (нужен WASI-хост/бэкенд, `DECISIONS_NEEDED.md`); package fetching (M13); архитектурные примеры; отладчик. `kernc new`, `kernc doc` (doctest) и `kernc run --watch` реализованы 2026-11-02.
- Языковые ограничения: `use dotnet.X[...]`, `in/ref/out`, identity для `unit`, запись через пользовательский `op []`, шаг срезов, `op in`, async-генераторы.

M7 закрыт 2026-11-01: `kern.events`, `kern.compress`, `kern.http` (оба варианта), `kern.threading`,
`kern.tasks`, `kern.parallel`, `kern.pipes`, моки `kern.test.mock`, базовая рефлексия
`kern.reflection` и `kern.build` (типы builder'а живут в `Kern.Cli/Build/BuildPrelude.cs`).

M9 закрыт 2026-11-01: полная рантайм-поверхность значения `type`, обёртки
`field_info/property_info/method_info/param_info/attribute_info`, статический `reflect.*`,
минимальный `define_type`, компиляторные `typeof(x)`/`property_names`/`method_names`/
`parent_names`, а также `kern.macro` и текстовый конвейер макросов (`#[macro]`,
`#[macro("expr")]`, `#[derive(name)]`, `name!(...)`, KRN1502/1503).

M12 закрыт: эталоны 09/18/23 и 04_flow, форматтер проверяется на идемпотентность
по всем примерам, `.compile-only` примеры 16/17/31-mathx проверяются отдельным тестом.
CI запускался; исправления первого прогона — `66fcc80` (async-отмена, пути в style-check, CRLF).

## Блокеры примеров (1 из 31 не проходит `kernc check`; 30 чистых — на 2026-11-02)

| Пример | Первый блокер | Что нужно |
|---|---|---|
| 07_async_errors | ✅ clean+run 2026-11-01 (async_iter + корутины) | — |
| 09_collections_json | ✅ clean+run 2026-11-01 | — |
| 11_workspace | ✅ clean+run 2026-11-01 (M8 builder) | — |
| 12_text_data | ✅ clean+run 2026-11-01 | — |
| 13_db_web | ✅ clean+run 2026-11-02 (`kern.backend` + `kern.db`; SQLite/ORM/HTTP) | — |
| 14_threads_compress | ✅ clean+run 2026-11-01 | — |
| 15_databases | ✅ clean+run 2026-11-02 (`kern.db.sqlite` + ORM); PostgreSQL/MySQL проверены `DbLiveTests` | для повторного live-прогона нужны `KERN_PG_URL`/`KERN_MYSQL_URL` |
| 16_mocks | ✅ `kernc test` 2026-11-01 (2 теста); `kernc run` печатает подсказку | — |
| 17_http_clients | ✅ clean+run 2026-11-01 (оба клиента) | — |
| 18_time | ✅ clean+run 2026-11-01 | — |
| 19_gui | stage-2 UI | вне первого прохода |
| 20_ffi | ✅ clean+run 2026-11-02 (`kern.ffi`: msvcrt strlen/abs/has) | — |
| 21_syntax_round15 | ✅ clean+run 2026-11-01 | — |
| 22_events_patterns | ✅ clean+run 2026-11-01 | — |
| 23_native_inheritance | ✅ clean+run 2026-11-01 (M6) | — |
| 24_reflection | ✅ clean+run 2026-11-01 (динамический builder минимален) | — |
| 25_transpile | ✅ clean+run 2026-11-02 (`kern.transpile`: C#/Kotlin, map_script, project) | — |
| 26_di | ✅ clean+run 2026-11-02 (`kern.di`) | — |
| 27_backend | ✅ clean+run 2026-11-02 (`kern.backend` + `kern.di`: контроллеры, JWT, middleware, SSE, WebSocket, `/stop`) | — |
| 28_macros | ✅ clean+run 2026-11-01 (M9 macros) | — |
| 29_async | ✅ clean+run 2026-11-02 (исправлена отмена awaited-задач, CI-флак) | — |
| 30_threads | ✅ clean+run 2026-11-01 | — |
| 31_kbc | ✅ clean+run 2026-11-02 (`kernc check` контейнера + `prebuild.txt`; `kernc dll mathx` → `kernc run app`) | — |

## Технические долги, влияющие на библиотеки

- **Native generic types** (`set<T>`, `queue<T>`, `stack<T>`, `observable<T>`, `collection<T>`): механизм закрыт в M6 — `[KernType(..., GenericArity = 1)]`, регистрация generic-символов, `[KernCtor]`-фабрики возвращают контейнер, `T` подставляется в членах; `collection<T>` — общий протокол с адаптерами.
- **Native inheritance/adapters**: родители берутся из C#-иерархии (`streamwriter/streamreader -> stream`), upcast и `is` сохраняют runtime-тип; `collection<T>` обслуживает list/array/dict/str/set/queue/stack/observable.
- **`iter<T>`/`async_iter<T>`** протоколы: и sync-, и async-циклы реализованы (M5/M7); `seq<T>` — нативный источник `iter<T>`.
- `PropertyDef` эмитится в `.kbc` (атрибуты свойств также копируются на аксессоры).
