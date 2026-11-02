# Kern LSP (MVP)

`src/Kern.Lsp` is a language server for Kern over JSON-RPC 2.0 on stdio (LSP base
protocol, `Content-Length` framing). It is an implemented tool (part of the stage-2
scope): the compiler and runtime are unchanged.

## Build and run

    dotnet build src/Kern.Lsp/Kern.Lsp.csproj
    src/Kern.Lsp/bin/Debug/net10.0/kern-lsp.exe

Или через основной CLI (рекомендуется):

    dotnet build src/Kern.Cli/Kern.Cli.csproj -c Release
    src/Kern.Cli/bin/Release/net10.0/kernc.exe lsp

`kernc lsp` и `kern-lsp` — один и тот же сервер: JSON-RPC 2.0 на stdin/stdout с
`Content-Length`. Сервер не печатает ничего, кроме протокола, поэтому его можно запускать
из любого редактора. Служебные флаги `vscode-languageclient` (`--stdio`,
`--clientProcessId=…`) игнорируются.

## VS Code

В VS Code нет встроенной поддержки LSP «из коробки» — нужен клиент. В репозитории есть
готовое минимальное расширение `editors/vscode`:

```powershell
cd editors/vscode
npm install
# откройте editors/vscode в VS Code и нажмите F5 (Run Extension)
```

По умолчанию расширение само ищет собранный `kernc` в `src/Kern.Cli/bin/Release|Debug/net10.0`,
затем пробует `kernc` из `PATH`. Если его нет — ошибка `spawn kernc ENOENT`: соберите
`dotnet build src/Kern.Cli/Kern.Cli.csproj -c Release` или укажите полный путь в
`kern.lsp.command` (см. пример ниже).

```json
{
	"kern.lsp.command": "C:/Users/ILNAR/Desktop/KernDS/src/Kern.Cli/bin/Release/net10.0/kernc.exe",
	"kern.lsp.args": ["lsp"]
}
```

Установка как обычного расширения: `vsce package` → `code --install-extension
kern-language-0.1.0.vsix`. Можно также взять любой универсальный LSP-клиент и прописать ему
команду `kernc lsp` (stdio).

## Why LSP

LSP даёт редактору то, чего не даёт обычный текстовый файл: ошибки компилятора прямо при
наборе, подсказки, переход к объявлению, список символов и форматирование. Сервер использует
тот же `ProjectCompiler`, что и `kernc check`, поэтому диагностика совпадает со сборкой.

## Capabilities

| Method | Behaviour |
|---|---|
| `initialize` | `textDocumentSync` = full, completion, signature help, hover, definition, formatting, document symbols |
| `textDocument/didOpen`, `didChange`, `didClose` | keeps open documents in memory and publishes diagnostics |
| `textDocument/publishDiagnostics` | `ProjectCompiler` over open documents **plus unopened `.kern` files of the project** (so `use` between files resolves); codes/messages/severity are compiler diagnostics |
| `textDocument/completion` | keywords, functions/types/globals with signatures, members after `.` (simple local type inference), module names after `use`, symbols inside `use mod[…]` |
| `textDocument/signatureHelp` | overload signatures and parameter labels for the call under the cursor |
| `textDocument/hover` | markdown signature for known functions/types, otherwise the source line |
| `textDocument/definition` | declaration of `fun`/`type`/`unit`, functions from imported files and members after `obj.` (cross-file, semantic index) |
| `textDocument/documentSymbol` | top-level functions, types, units and globals with ranges |
| `textDocument/formatting` | full-document edit from `SourceFormatter` (tabs/CRLF/trailing spaces) |
| `shutdown` / `exit` | standard lifecycle |

## Syntax highlighting

Подсветка не входит в LSP: её даёт TextMate-грамматика расширения
`editors/vscode/syntaxes/kern.tmLanguage.json` (ключевые слова, типы, строки с префиксами
`r/f/x/v` в любом порядке, `${…}`, `#[…]`, числа, операторы). После изменения грамматики
перезапустите окно расширения.

## Limits (MVP)

- Проект загружается из `src/**/*.kern` (иначе `*.kern` в корне) плюс открытые документы;
  внешние `Kern.Lib.*.dll` ищутся в `libs/**` и `libs/_out/**`. Если DLL не загрузилась,
  LSP продолжает работать с ядром.
- Completion после точки выводит тип локальной переменной/параметра простым текстовым
  разбором (`Type name = …`, `fun(Type name)`, `var name = Type(…)`); сложные выражения и
  цепочки `a.b.c.` пока не выводятся. Для примитивов (`i32.`, `bool.`, `f64.`) показываются
  универсальные `to_str`/`to_debug`, для `str`/`char`/коллекций — их нативные члены.
- Signature help — все перегрузки функции/метода по имени перед `(`; показываются
  параметры и активный аргумент (по запятой).
- `build.kern` анализируется с встроенным `build_prelude` (заглушка `__build_env`),
  поэтому `workspace`, `target`, `profile` и их члены видны в редакторе.
- Hover/definition: для известных функций/типов используется семантический индекс (включая
  импортированные файлы и члены после `.`); если имя не найдено в индексе, definition ищет
  объявление в текущем файле по токенам.
- Нет references, rename, semantic tokens или code actions.
- Formatting is the conservative `kernc fmt` formatter, not a pretty-printer.

## Tests

`src/Kern.Tests/Features/LspTests.cs` drives `LspServer.Handle` directly: capabilities,
diagnostics publication, symbols, definition, completion, hover and formatting.
