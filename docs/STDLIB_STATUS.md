# STDLIB_STATUS — библиотеки и что они разблокируют

Карта на 2026-11-01: языковое ядро M1–M3 закрыто, дальше идут библиотеки (M6/M7) и CLI/builder (M8).
Таблица помогает выбирать следующий срез по эффекту на `examples/`.

## Реализовано (частично)

| Модуль | Что есть | Ограничения |
|---|---|---|
| `kern.core` | `print`, `println`, `eprintln`, `panic`, `delay`, `exit`, `format`, `to_str`, `typeof`, `name_of` | — |
| `kern.io` | `streamwriter`/`streamreader` (`write`, `write_line`, `read_line`, `read_all`, `read_text`, `close`, `dispose`, `is_open`, `at_end`), `ConsoleModule` | файловые `file.*` ещё не добавлены |
| `kern.test` | `assert`, `assert_eq`, `assert_ne`, `assert_near`, `assert_nil`, `assert_not_nil`, `fail` | `fail` типизирован `void`, а не `never` |
| `kern.math` | базовые функции | — |
| `kern.collections` | нативные `list`/`array`/`dict` (включая `first/last/get/set/map/filter/keys/values/get_or`), члены `str`/`char`; generic-контейнеры `set<T>`, `queue<T>`, `stack<T>`, `observable<T>` (add/remove/clear/on_change); общий протокол `collection<T>` (len/is_empty/contains/to_list) с адаптерами и итерацией | — |
| `kern.text` | `len/upper/lower/trim*/contains/starts_with/ends_with/replace/repeat/split/lines/join/chars/from_chars/parse_i32/i64/f64/pad_left/right` | — |
| `kern.numerics` | `decimal` и `bigint`: фабрики из str/i64/f64, арифметика, сравнения, `to_str/to_f64/to_i64`, `abs/round/truncate/pow/compare` | `-0`/переполнение decimal — как в .NET |
| `str`/`char` (встроенные) | `len`, `upper`, `lower`, `trim*`, `contains`, `starts_with`, `ends_with`, `index_of`, `substring`, `replace`, `split`, `chars`, `lines`, `join`, `pad_left/right`, `repeat`, `to_str`, `to_i32/to_i64/to_f64/to_bool` | `str` пока не все члены из старых примеров |
| `kern.regex` | `regex(pattern[, flags])` → `regex` (panic по-русски), `try_new`, члены `is_match/find/find_all/find_match/replace/split`, свойства `pattern/options`; `x"…"`/`fx"…"` lowering | `find_match` (а не `match` — ключевое слово), Q32; `regex.try_new(…)` пока только `try_new(…)` |
| `kern.json` | `to_str`, `to_str_pretty`, `parse` → `dict<str, object>`/`list<object>`, `from_str<T>` (reified `[KernTypeArgs]`) | `Err` только при ошибке разбора |
| `kern.env` | `get`, `get_or`, `set`, свойства `os`, `cwd`, `home` | — |
| `file.*` / `kern.path` | чтение/запись/список/копирование/удаление (IO → `result<T, str>`); `join/dir_name/file_name/ext/normalize/is_absolute/absolute` | — |
| `kern.csv` / `kern.yaml` / `kern.xml` | CSV RFC 4180 (`parse`, `parse_dicts`, `to_str`); YAML-подмножество (`parse`, `to_str`); XML (`parse`, `new`, `xml_node.name/text/children/attr/child/find_all/add`) | YAML без якорей/тегов; XML — только элементы и атрибуты |
| `kern.crypto` / `kern.uuid` | sha1/sha256/sha512/md5, hmac_sha256, base64, random_hex, hash_password/verify_password; `uuid.new()` | — |
| `kern.time` | `duration` (parse/from_*/total_*/арифметика/сравнения/печать), `datetime` (of/now/поля/weekday/format/add_months/±/сравнения), `stopwatch` (запускается сразу), `time.sleep/measure/unix_*` | нативные параметры по умолчанию не поддерживаются: `datetime.of` требует все 6 аргументов |

## Отсутствует (stage-2 и другие этапы)

`kern.db.*`, `kern.ui`, `kern.ffi`, `kern.transpile`, `kern.di`, `kern.backend`, `kern.mod`,
`kern.stub`, `kern.macro` (M9), `kern.web`/`kern.server` (stage-2).

M7 закрыт 2026-11-01: `kern.events`, `kern.compress`, `kern.http` (оба варианта), `kern.threading`,
`kern.tasks`, `kern.parallel`, `kern.pipes`, моки `kern.test.mock`, базовая рефлексия
`kern.reflection` и `kern.build` (типы builder'а живут в `Kern.Cli/Build/BuildPrelude.cs`).

## Блокеры примеров (9 из 31 не проходят `kernc check`; 22 чистых — на 2026-11-01)

| Пример | Первый блокер | Что нужно |
|---|---|---|
| 07_async_errors | ✅ clean+run 2026-11-01 (async_iter + корутины) | — |
| 09_collections_json | ✅ clean+run 2026-11-01 | — |
| 11_workspace | `build.kern`/`workspace` | M8 builder |
| 12_text_data | ✅ clean+run 2026-11-01 | — |
| 13_db_web | старый `use path` | refresh примеров + `kern.db` |
| 14_threads_compress | ✅ clean+run 2026-11-01 | — |
| 15_databases | неизвестные функции | `kern.db.*` |
| 16_mocks | ✅ check 2026-11-01 (тесты — `#[test]`, запуск позже) | — |
| 17_http_clients | ✅ clean+run 2026-11-01 (оба клиента) | — |
| 18_time | ✅ clean+run 2026-11-01 | — |
| 19_gui | stage-2 UI | вне первого прохода |
| 20_ffi | stage-2 FFI | вне первого прохода |
| 21_syntax_round15 | ✅ clean+run 2026-11-01 | — |
| 22_events_patterns | ✅ clean+run 2026-11-01 | — |
| 23_native_inheritance | ✅ clean+run 2026-11-01 (M6) | — |
| 24_reflection | ✅ clean+run 2026-11-01 (динамический builder минимален) | — |
| 25_transpile | старый `use path` | refresh примеров + `kern.transpile` |
| 26_di | `container` | `kern.di` |
| 27_backend | stage-2 | вне первого прохода |
| 28_macros | stage-2 macros | M9 |
| 29_async | ✅ clean+run 2026-11-01 | — |
| 30_threads | ✅ clean+run 2026-11-01 | — |
| 31_kbc | `check` без KRN-кодов | разобрать layout/`build.kern` (M8) |

## Рекомендуемый порядок

1. `kern.regex` + lowering литералов → пример 21.
2. `kern.json`, `kern.env`, `file.*` → пример 09 после контейнеров.
3. M6 generic native types + `collection<T>`/`iter`/`async_iter` адаптеры → примеры 23/09/07/12.
4. `kern.time`, `kern.csv`/`yaml`, `kern.events`, `kern.di` → примеры 18/12/22/26.
5. M8 `build.kern`/workspace/`exe`/`dll` → примеры 11/13/25/31.
6. M9 рефлексия/макросы, `kern.http`/`db`/`threading`/`compress` — по остаточным примерам.

## Технические долги, влияющие на библиотеки

- **Native generic types** (`set<T>`, `queue<T>`, `stack<T>`, `observable<T>`, `collection<T>`): механизм закрыт в M6 — `[KernType(..., GenericArity = 1)]`, регистрация generic-символов, `[KernCtor]`-фабрики возвращают контейнер, `T` подставляется в членах; `collection<T>` — общий протокол с адаптерами.
- **Native inheritance/adapters**: родители берутся из C#-иерархии (`streamwriter/streamreader -> stream`), upcast и `is` сохраняют runtime-тип; `collection<T>` обслуживает list/array/dict/str/set/queue/stack/observable.
- **`iter<T>`/`async_iter<T>`** протоколы: sync-цикл реализован; async — M5.
- `PropertyDef` не эмитится в `.kbc` (атрибуты свойств на аксессорах).
