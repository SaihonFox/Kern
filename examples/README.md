# Примеры Kern

Каждая папка — отдельный проект по соглашению (`src/` + `main.kern`; `kern.toml` упразднён, настройки — в необязательном `build.kern`). Запуск: `kernc run examples/01_basics`.

**Golden-тесты (первый этап):** рядом с `src/` лежит `expected.out` (создаёт QA-агент по спецификации; см. `docs/TESTING.md`). `.compile-only` означает только проверку типов, `.pending` — ожидаемый вывод ещё не согласован; `prebuild.txt` задаёт команды `kernc` для предварительной сборки.

Примеры 13, 19, 20, 25, 27 по-прежнему требуют внешних библиотек/серверов (`kern.ui`, `kern.ffi`, `kern.transpile`, `kern.backend`); 15 (`kern.db*`) и 26 (`kern.di`) собираются внешними `libs/Kern.Lib.Db` / `libs/Kern.Lib.Di` и запускаются после `dotnet build libs/Kern.Libs.slnx`. `kernc build` кладёт `.kbc` в `<пример>/out/`.

| Папка | Тема |
|---|---|
| 01_basics | переменные, строки, интерполяция, `**`, `nil`/`none`, `?:`, smart cast, `T!` |
| 02_types | коллекции, кортежи, деструктуризация, union, `in`/`is` |
| 03_units | `unit`/`unit!`, операторы, деструктуризация, union из unit («enum с данными»), generic unit |
| 04_flow | `if`/`match`/`loop` как выражения, диапазоны, `stop`/`next` |
| 05_functions | значения по умолчанию, именованные аргументы, замыкания, variadic, расширения, `op i32.*` |
| 06_generics_operators | классы, `@type`, свойства, цепочка конструкторов `: $(...)`, `$[A]member`, generics, `typeof<T>()` |
| 07_async_errors | `async`/`await`, `result`, `ret`, `panic` |
| 08_multi_module | несколько файлов, `use`, `pub` |
| 09_collections_json | dict, set/queue/stack (нативные, `collection<T>`), json, файлы, env |
| 10_testing | `#[test]`, `kern.test`, запуск `kernc test` |
| 11_workspace | несколько целей, `build.kern`, задачи, зависимости |
| 12_text_data | regex, csv, yaml, xml, crypto, uuid |
| 13_db_web | SQLite, мини-ORM, HTTP-сервер |
| 15_databases | SQLite, PostgreSQL, MySQL: единый интерфейс, мини-ORM, async-запросы |
| 16_mocks | `mock.create<T>()`, `returns/returns_for/handle/verify`, тесты без ручных заглушек |
| 17_http_clients | `http_client`: `get<T>/post<T>` и прокси по атрибутам `#[get("/x/{id}")]` |
| 18_time | `duration`, `datetime`, `stopwatch`, `time.measure`; время программы — `kernc run --time` |
| 19_gui | окно на Avalonia: `kern.ui` — `label/button/input/checkbox/slider/progress/list`, `column/row`, обработчики; тест проверяет только компиляцию |
| 20_ffi | вызов функций из C/C++ DLL: `kern.ffi` (`native_lib`, `fn("имя", "i32(str)")`, `call`) |
| 14_threads_compress | потоки в изолированных VM, `channel`, `mutex`, `atomic`, `semaphore`, gzip |
| 21_syntax_round15 | префиксы строк, do-while, `match` с `:`-охраной, `use` + `dispose()` |
| 22_events_patterns | `event`/`signal`, `observable`, структурный `match` по именам полей |
| 23_native_inheritance | `collection<T>`, `stream` и иерархия нативных типов |
| 24_reflection | рефлексия: `typeof`, `reflect.*`, `define_type`, `name_of<T>()` и др. |

Примечание: примеры написаны по `docs/spec/kern.en.md` **без запуска** (у автора не было `dotnet`): они могут содержать ошибки. Если пример противоречит спецификации — правьте пример (и сообщите lead), спорное выносите в `DECISIONS_NEEDED.md`. Ожидаемый вывод выводите из спецификации, а не из того, что печатает реализация.

## 11_workspace — несколько целей и build.kern
`kernc tasks examples/11_workspace` — список задач; `kernc build examples/11_workspace` — собрать все цели
(повторная сборка пропускается, если исходники не менялись); `kernc run examples/11_workspace app -- Мир`;
`kernc do app:hello release examples/11_workspace`.

## 12_text_data, 13_db_web — библиотеки
12: regex, csv, yaml, xml, crypto, uuid. 13: SQLite + мини-ORM (`orm.table<User>`) и HTTP-сервер (`web_server`, `await s.serve()`).

## 21_syntax_round15 — синтаксис раунда 15
Префиксы строк, do-while `loop { }: cond`, ветки `match` с `Тип x: cond =>`, `use` + `dispose()`.

## 22_events_patterns — события и паттерны
`event<T>`/`signal` с `+=`/`-=`, `observable<T>`, структурный match `Point(x: 0, y: var y)`.

## 23_native_inheritance — наследование нативных типов
`collection<T>` (set/queue/stack/observable) и `stream` (streamwriter/streamreader).

## 24_reflection — рефлексия
`typeof<T>()`, `t.fields`/`methods`/`attributes`, `get`/`set`/`call`/`create`, `reflect.types_with_attribute`, `reflect.define_type` (типы на лету), `name_of<T>()`/`field_names<T>()` (compile-time).

## 25_transpile — транспилятор
`transpiler("csharp"|"kotlin")`, `.file`/`.text`/`.project`, `.map_script` с пользовательскими `map_type/map_call/map_member` (нужна собранная библиотека `kern.transpile`, см. libs/).

## 26_di — внедрение зависимостей
`container`: фабрики и типы (`singleton`/`transient`/`scoped`, `*_as`), атрибуты `#[singleton]` + `scan()` и `#[inject]`, области и `dispose` (нужна собранная библиотека `kern.di`).

## 27_backend — backend-фреймворк
`app` + middleware (`logger`, `cors`, `jwt_optional`, свой `fun(ctx, chain)`), группы, атрибутный контроллер с DI (`#[controller]`, `#[get]`, `#[auth]`, `#[roles]`), JWT-логин, SSE и WebSocket. Запуск и curl-примеры — в шапке `main.kern`.

## 28_macros — макросы
`#[macro]`-функции над описанием типа: `#[derive(to_text)]`, генерация новой функции (`m.top`), диагностика (`m.error`), макрос-выражение `sq!(3 + 1)`. Файл `macros.kern` содержит макросы, `main.kern` их применяет.

## 29_async — расширенный async
`tasks.all/any/race/settled/select/timeout/retry`, `cancel_token`, `pipe<T>` + `async loop`, `parallel.map`, `pool`.

## 30_threads — потоки с общей памятью
`thread.spawn` с замыканиями, `use var g = m.guard()`, `rw_lock`, `amutex`, `atomic`, `shared<T>`, `conc_list/dict/queue`, `parallel.for_range/reduce`, `thread.run` (async + потоки).

## 31_kbc — библиотека .kbc через `use`
`mathx` собирается `kernc dll`, приложение подключает её в `app/build.kern` (`t.kbc("../mathx/out/mathx.kbc")`) и использует обычным `use mathx.vec[...]`.
