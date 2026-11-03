# Self-hosted Kern compiler (bootstrap)

Первый срез компилятора Kern, написанного на самом Kern. Сейчас реализован **лексер**;
дальше — парсер, AST, binder/typecheck, байткод и собственная VM.

## Запуск

```sh
kernc run bootstrap
kernc run bootstrap bootstrap/src/lexer.kern
kernc test bootstrap
```

`main` объявляет `#[needs(fs.read("."))]` и читает путь из первого аргумента; без
аргумента печатает токены встроенного примера.

## Что уже есть

- `src/tokens.kern` — тип `token(kind, text, line, column)`.
- `src/lexer.kern` — `lex(str) -> list<token>`:
  - идентификаторы/числа/строки;
  - однострочные `//`-комментарии;
  - пробелы и переводы строк с отслеживанием `line`/`column`;
  - одно- и двухсимвольные операторы.
- `#[test]`-функции проверяют число, ключевое слово и комментарии.

## Следующие шаги

1. Парсер выражений (Pratt) и AST-узлы.
2. Объявления `fun`/`type`/`unit`, области видимости.
3. Binder: типы, перегрузки, диагностики.
4. Генерация `.kbc` и запуск в существующей VM.
5. Bootstrap: собрать `kernc` самим `kernc` и сравнить golden-выходы.
