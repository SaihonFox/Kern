# Kern LSP (MVP)

`src/Kern.Lsp` is a language server for Kern over JSON-RPC 2.0 on stdio (LSP base
protocol, `Content-Length` framing). It is a stage-2 tool: the compiler and runtime
are unchanged.

## Build and run

    dotnet build src/Kern.Lsp/Kern.Lsp.csproj
    src/Kern.Lsp/bin/Debug/net10.0/kern-lsp.exe

Point a generic LSP client at `kern-lsp.exe` and open a `.kern` file.

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
