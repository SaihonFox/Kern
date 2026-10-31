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
| `kern.collections` | нативные `list`/`array`/`dict` (включая `first/last/get/set/map/filter/keys/values/get_or`), члены `str`/`char` | нет `set/queue/stack/observable`, нет `collection<T>`-протокола/адаптеров |
| `str`/`char` (встроенные) | `len`, `upper`, `lower`, `trim*`, `contains`, `starts_with`, `ends_with`, `index_of`, `substring`, `replace`, `split`, `chars`, `lines`, `join`, `pad_left/right`, `repeat`, `to_str`, `to_i32/to_i64/to_f64/to_bool` | `str` пока не все члены из старых примеров |
| `kern.regex` | в работе (`regex(pattern[, flags])`, `is_match/find/find_all/replace/split`) | lowering литералов `x"…"`/`fx"…"` — следующий компиляторный срез |

## Отсутствует (M7 и stage-2)

`kern.json`, `kern.env`, `kern.time`, `kern.path`, `kern.process`, `kern.cli`, `kern.config`, `kern.log`, `kern.ansi`,
`kern.csv`, `kern.yaml`, `kern.toml`, `kern.ini`, `kern.xml`, `kern.uuid`, `kern.crypto`, `kern.compress`,
`kern.net`, `kern.http`, `kern.server`, `kern.web`, `kern.db.*`, `kern.threading`, `kern.tasks`, `kern.pipes`,
`kern.events`, `kern.reflection`, `kern.macro`, `kern.di`, `kern.build`, `kern.ffi`, `kern.transpile`, `kern.ui`,
`kern.backend`, `kern.mod`, `kern.stub`.

## Блокеры примеров (23 из 31 не проходят `kernc check`)

| Пример | Первый блокер | Что нужно |
|---|---|---|
| 07_async_errors | `async_iter<T>` loop, `await` вне async | M5 источник/`async_iter` + `kern.tasks` |
| 09_collections_json | `set/queue/stack` | M6 generic native types + `kern.collections`; затем `kern.json`, `kern.env`, `file.*` |
| 11_workspace | `build.kern`/`workspace` | M8 builder |
| 12_text_data | `regex`, `csv`, `yaml`, `loop` по `T` | `kern.regex` + `kern.csv`/`yaml` + `iter<T>` на generic-параметре |
| 13_db_web | старый `use path` | refresh примеров + `kern.db` |
| 14_threads_compress | `KRN1001` типы | `kern.compress`, `kern.threading` |
| 15_databases | неизвестные функции | `kern.db.*` |
| 16_mocks | `mock` | `kern.mock`/proxy (M9) |
| 17_http_clients | `#[get]` и `http_client` | `kern.http` + атрибуты HTTP |
| 18_time | `time` | `kern.time` |
| 19_gui | stage-2 UI | вне первого прохода |
| 20_ffi | stage-2 FFI | вне первого прохода |
| 21_syntax_round15 | regex literal | `kern.regex` + lowering `x"…"`/`fx"…"` |
| 22_events_patterns | `KRN1001` | `kern.events` |
| 23_native_inheritance | `collection<i32>` | M6 native inheritance + контейнеры |
| 24_reflection | `typeof(x)`/`reflect.*` | M9 runtime reflection |
| 25_transpile | старый `use path` | refresh примеров + `kern.transpile` |
| 26_di | `container` | `kern.di` |
| 27_backend | stage-2 | вне первого прохода |
| 28_macros | stage-2 macros | M9 |
| 29_async | `KRN1001` | `kern.tasks` |
| 30_threads | `KRN1001` | `kern.threading` |
| 31_kbc | `check` без KRN-кодов | разобрать layout/`build.kern` (M8) |

## Рекомендуемый порядок

1. `kern.regex` + lowering литералов → пример 21.
2. `kern.json`, `kern.env`, `file.*` → пример 09 после контейнеров.
3. M6 generic native types + `collection<T>`/`iter`/`async_iter` адаптеры → примеры 23/09/07/12.
4. `kern.time`, `kern.csv`/`yaml`, `kern.events`, `kern.di` → примеры 18/12/22/26.
5. M8 `build.kern`/workspace/`exe`/`dll` → примеры 11/13/25/31.
6. M9 рефлексия/макросы, `kern.http`/`db`/`threading`/`compress` — по остаточным примерам.

## Технические долги, влияющие на библиотеки

- **Native generic types** (`set<T>`, `queue<T>`, `stack<T>`, `observable<T>`, `collection<T>`): `NativeSymbolBuilder.RegisterTypes` сейчас отвергает generic-параметры у нативных типов — это M6-срез.
- **Native inheritance/adapters** (`collection<T>` как родитель для list/str/контейнеров) — M6.
- **`iter<T>`/`async_iter<T>`** протоколы: sync-цикл реализован; async — M5.
- `PropertyDef` не эмитится в `.kbc` (атрибуты свойств на аксессорах).
