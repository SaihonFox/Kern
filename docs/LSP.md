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
из любого редактора.

## VS Code

В VS Code нет встроенной поддержки LSP «из коробки» — нужен клиент. В репозитории есть
готовое минимальное расширение `editors/vscode`:

```powershell
cd editors/vscode
npm install
# откройте editors/vscode в VS Code и нажмите F5 (Run Extension)
```

По умолчанию оно запускает `kernc lsp`. Если `kernc` не в `PATH`, укажите путь в настройках:

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
| `initialize` | `textDocumentSync` = full, completion, hover, definition, formatting, document symbols |
| `textDocument/didOpen`, `didChange`, `didClose` | keeps open documents in memory and publishes diagnostics |
| `textDocument/publishDiagnostics` | `ProjectCompiler` over all open documents; codes/messages/severity are compiler diagnostics |
| `textDocument/completion` | Kern keywords + identifiers of the current document |
| `textDocument/hover` | markdown code block with the source line under the cursor |
| `textDocument/definition` | declaration of `fun`/`type`/`unit` (and `fun Type.name`) by name |
| `textDocument/documentSymbol` | top-level functions, types, units and globals with ranges |
| `textDocument/formatting` | full-document edit from `SourceFormatter` (tabs/CRLF/trailing spaces) |
| `shutdown` / `exit` | standard lifecycle |

## Limits (MVP)

- The native catalog is `Kern.Runtime` only; external `Kern.Lib.*` modules
  (`kern.backend`, `kern.db`, ...) are not loaded yet, so `use kern.backend[...]`
  reports unresolved names.
- All open documents form one project; `RelativePath` is relative to the workspace
  root (or just the file name when there is no root).
- Hover and definition are syntactic (token/line based), not type-aware; references,
  rename, semantic tokens, signature help and code actions are not implemented.
- Formatting is the conservative `kernc fmt` formatter, not a pretty-printer.

## Tests

`src/Kern.Tests/Features/LspTests.cs` drives `LspServer.Handle` directly: capabilities,
diagnostics publication, symbols, definition, completion, hover and formatting.
