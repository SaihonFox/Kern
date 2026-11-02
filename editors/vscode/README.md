# Kern для VS Code (LSP-клиент)

Это минимальное расширение-клиент: само расширение не компилирует код, а запускает
`kernc lsp` и показывает диагностику, автодополнение, hover, переход к определению,
символы документа и форматирование.

## 1. Соберите kernc

Из корня репозитория:

```powershell
dotnet build src/Kern.Cli/Kern.Cli.csproj -c Release
```

`kernc` появится в `src/Kern.Cli/bin/Release/net10.0/` (`kernc.exe` на Windows).

## 2. Поставьте зависимости расширения

```powershell
cd editors/vscode
npm install
```

## 3. Запустите расширение в режиме разработки

1. Откройте папку `editors/vscode` в VS Code.
2. Нажмите **F5** («Run Extension»). Откроется новое окно VS Code.
3. В новом окне откройте папку с `.kern`-файлами.

По умолчанию расширение само находит собранный `kernc` (Release/Debug в
`src/Kern.Cli/bin/net10.0`), а если не находит — пробует `kernc` из `PATH`. Если `kernc`
лежит в другом месте, задайте в настройках нового окна:

```json
{
	"kern.lsp.command": "C:/Users/ILNAR/Desktop/KernDS/src/Kern.Cli/bin/Release/net10.0/kernc.exe",
	"kern.lsp.args": ["lsp"]
}
```

На Linux/macOS путь заканчивается на `.../net10.0/kernc`.

### Ошибка `spawn kernc ENOENT`

Значит `kernc` не найден ни автоматически, ни в `PATH`:

1. Соберите его: `dotnet build src/Kern.Cli/Kern.Cli.csproj -c Release` и перезапустите
   расширение (F5).
2. Либо пропишите абсолютный путь в `kern.lsp.command` (пример выше).
3. Проверьте, что `kern.lsp.args` равен `["lsp"]`.

## 4. Установка как обычного расширения

```powershell
npm install -g @vscode/vsce
cd editors/vscode
vsce package
code --install-extension kern-language-0.1.0.vsix
```

Либо используйте любой универсальный LSP-клиент и укажите ему команду `kernc lsp`
(stdio, JSON-RPC). Расширению нужен только запущенный `kernc`; отдельный `kern-lsp`
собирать не обязательно.

## Что уже работает

- подсветка синтаксиса (TextMate: ключевые слова, типы, строки с `r`/`f`/`x`/`v`,
  интерполяция `${…}`, атрибуты `#[…]`, числа, операторы);
- диагностика компилятора (`KRN…`) при открытии/изменении файла;
- импорты между `.kern`-файлами проекта (LSP читает `src/**/*.kern` с диска) и внешние
  `Kern.Lib.*` из `libs/`;
- автодополнение: функции/типы/сигнатуры, члены после `obj.`, модули после `use`,
  символы внутри `use module[…]`; для `i32.`/`bool.` доступны `to_str`/`to_debug`;
- signature help — подсказка аргументов при `(` и `,`;
- `build.kern`: видны `workspace`, `target`, `profile` и их члены;
- hover: сигнатуры функций/типов;
- переход к определению `fun`/`type`/`unit`, функций из импортированных файлов и членов
  после `obj.`;
- символы документа (функции, типы, unit, глобалы);
- форматирование (табы/CRLF, хвостовые пробелы, как `kernc fmt`).

## Ограничения MVP

- hover/переход к определению синтаксические, не типовые;
- нет references/rename/semantic tokens/code actions;
- completion после `.` понимает простые объявления (`Type x = …`, параметры,
  `var x = Type(…)`), но не цепочки `a.b.c.` и сложные выражения;
- форматтер консервативный (не pretty-printer).

## Лицензия

MIT, см. `LICENSE` в этой папке и в корне репозитория.
