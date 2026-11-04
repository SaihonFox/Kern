# REMAINING — что сделано и что осталось (2026-11-04)

Короткая сводка для owner. Подробности ограничений — `docs/KNOWN_ISSUES.md` §E,
открытые решения — `DECISIONS_NEEDED.md`, свежие изменения — `CHANGELOG_LANG.md`.

## Сделано

- **Stage 1 (M0–M12) и M13/M14** закрыты: лексер, парсер, binder, типы, codegen,
  `.kbc` v4, VM, async/threads, std, CLI, builder, `kernc dll`, fmt, repl, optimizer,
  NativeAOT, goldens, CI.
- **Std на Kern:** `kern.ansi`, `kern.csv`, `kern.parse` (автоподключение через
  `StdSources`); C# `CsvApi`/`ParseApi` удалены. `kern.text`/`kern.num` пока на C#.
- **Runtime type args:** `typeof<T>()`, вложенная передача `T`, виртуальные
  generic-методы (`CallGeneric`/`CallVirtualGeneric`/`TypeOfParam`). Generic-функция
  не является значением — `CallValue` generic-целей не имеет.
- **`use dotnet`:** статические и экземплярные члены, конструкторы, сеттеры свойств,
  статические члены обычных классов; открытые generic-типы (`List<i32>`,
  `Dictionary<str, i32>`), расширенная форма `params`, `ref`/`out` через `*T`,
  делегаты `Action`/`Func`/`Predicate`/`Comparison`/`Converter`; имена как в C#
  (`b.Length`, `File.ReadAllText`, `String.Join`).
- **Модули:** `use` открывает квалифицированный доступ по короткому имени
  (`ini.parse(...)`, `util.twice(...)`); локальная переменная/тип важнее модуля.
- **Индексаторы:** `op []`/`op []=` и их extension-форма `op TypeName.[]`; чтение и
  сеттеры используют общий резолвер перегрузок (`KRN1402` при неоднозначности).
- **Протоколы:** `hash`/`compare`/`clone`/`to_str`/`to_debug` проверяют арность и тип
  результата при объявлении (`KRN2101`).
- **Атрибуты:** цели и аргументы встроенных атрибутов проверяются (`KRN2101`),
  неизвестные имена — `KRN2307`; `#[derive]` дополнительно проверяет протоколы.
- **Язык:** `op []=`, модульные атрибуты, `#[needs]`/`--deny`, `seq.len`/повторная
  итерация, `in`/`ref`, графемы `str`, вложенная деструктуризация, `with` для
  auto-property и вычисляемых свойств с `set`, открытые срезы, `str`↔`list<char>`,
  `fail: never`, константные преобразования, `!`/`!is`/`T!` разбор.
- **Инструменты:** `kernc test` прокачивает async-тесты; одиночный файл идёт через
  общий `CompileFiles` (макросы + `Kern.Std`); профайлер считает self-time;
  read-индексаторы используют общий резолвер (`KRN1402`); `kern.regex.escape`.
- **Модули/сборка:** статические инициализаторы всех файлов проекта склеиваются и
  выполняются до `main`; типы в `.kbc` сопоставляются по полному имени
  (`module.Type`), поэтому библиотека и потребитель могут иметь одноимённые типы.
- **Отладчик:** имена локальных слотов пишутся в `.kbc` v4; `locals` показывает имена,
  `print` принимает имя/слот и простые выражения (`+ - * / %`, литералы, скобки).
- **Актуальное состояние:** build 0 errors, suite **705/705**, style clean.

## Осталось — нужно решение owner

1. **`kern.text`/`kern.num` → Kern.** Решено оставить на C# (owner разрешил fallback):
   полный перенос требует нативных примитивов (графемы, Unicode-нормализация, UTF-8,
   комплексные числа/FFT/QR). API не менялся; перенос возможен отдельным срезом.
2. **Полнота std:** BLAKE3/Ed25519/argon2/bcrypt, parser-комбинаторы в `kern.parse`.
   Варианты: задокументировать, NuGet или своя реализация (XL + крипто-ревью).
3. **«Настоящие» C#-генерики (variant 2):** reified `KType[]` в `KNativeBox`.
4. **Stage-2:** `kern.ui` (Avalonia) и `kernc wasm` (NetWasm).

## Осталось — можно делать без решения (по приоритету)

- Capabilities: реальная проверка path-scope `fs.read("…")`/`fs.write("…")`.
- Профайлер: call tree; отладчик: attach/remote и полные Kern-выражения (сейчас только
  литералы/локальные/арифметика).
- `use dotnet`: generic-методы BCL (сейчас пропускаются), делегаты из CLR в Kern,
  открытые generic-типы с аргументами-классами не из `use dotnet`.
- Материализация derived `hash`/`compare`/`clone` в члены/рефлексию; `reflect.define_type`
  с методами/свойствами; `replace(fun)` в `kern.regex`.
