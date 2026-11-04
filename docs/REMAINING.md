# REMAINING — что сделано и что осталось (2026-11-04)

Короткая сводка для owner. Подробности ограничений — `docs/KNOWN_ISSUES.md` §E,
открытые решения — `DECISIONS_NEEDED.md`, свежие изменения — `CHANGELOG_LANG.md`.

## Сделано

- **Stage 1 (M0–M12) и M13/M14** закрыты: лексер, парсер, binder, типы, codegen,
  `.kbc` v3, VM, async/threads, std, CLI, builder, `kernc dll`, fmt, repl, optimizer,
  NativeAOT, goldens, CI.
- **Std на Kern:** `kern.ansi`, `kern.csv`, `kern.parse` (автоподключение через
  `StdSources`); C# `CsvApi`/`ParseApi` удалены. `kern.text`/`kern.num` пока на C#.
- **Runtime type args:** `typeof<T>()`, вложенная передача `T`, виртуальные
  generic-методы (`CallGeneric`/`CallVirtualGeneric`/`TypeOfParam`). Generic-функция
  не является значением — `CallValue` generic-целей не имеет.
- **`use dotnet`:** статические и экземплярные члены, конструкторы, сеттеры свойств,
  статические члены обычных классов, `params` как `array<T>`; имена как в C#
  (`b.Length`, `File.ReadAllText`, `String.Join`).
- **Индексаторы:** `op []`/`op []=` и их extension-форма `op TypeName.[]`; чтение и
  сеттеры используют общий резолвер перегрузок (`KRN1402` при неоднозначности).
- **Протоколы:** `hash`/`compare`/`clone`/`to_str`/`to_debug` проверяют арность и тип
  результата при объявлении (`KRN2101`).
- **Язык:** `op []=`, модульные атрибуты, `#[needs]`/`--deny`, `seq.len`/повторная
  итерация, `in`/`ref`, графемы `str`, вложенная деструктуризация, `with` для
  auto-property и вычисляемых свойств с `set`, открытые срезы, `str`↔`list<char>`,
  `fail: never`, константные преобразования, `!`/`!is`/`T!` разбор.
- **Инструменты:** `kernc test` прокачивает async-тесты; одиночный файл идёт через
  общий `CompileFiles` (макросы + `Kern.Std`); профайлер считает self-time;
  read-индексаторы используют общий резолвер (`KRN1402`); `kern.regex.escape`.
- **Актуальное состояние:** build 0 errors, suite **658/658**, style clean.

## Осталось — нужно решение owner

1. **`kern.text`/`kern.num` на Kern.** Нужен qualified `mod.member` для source-модулей
   (рекомендуется) либо breaking-перенос на `use kern.text[upper]`.
2. **Полнота std:** BLAKE3/Ed25519/argon2/bcrypt, parser-комбинаторы в `kern.parse`.
   Варианты: задокументировать, NuGet или своя реализация (XL + крипто-ревью).
3. **«Настоящие» C#-генерики (variant 2):** reified `KType[]` в `KNativeBox`.
4. **Stage-2:** `kern.ui` (Avalonia) и `kernc wasm` (NetWasm).

## Осталось — можно делать без решения (по приоритету)

- Валидация аргументов/целей атрибутов (сейчас полностью проверяется только `derive`).
- `use dotnet`: открытые generic-типы BCL, by-ref/out, делегаты; variadic-синтаксис
  для `params`.
- Capabilities: реальная проверка path-scope `fs.read("…")`/`fs.write("…")`.
- Профайлер: call tree; отладчик: attach/remote/expression eval, имена локальных.
- Квалифицированный доступ к source-модулям (снимает блокер с `text`/`num`).
- Материализация derived `hash`/`compare`/`clone` в члены/рефлексию; `reflect.define_type`
  с методами/свойствами; `replace(fun)` в `kern.regex`.
- Один модульный static initializer → несколько; конфликт одноимённых типов в `kernc dll`.
