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

Если `kernc` не в `PATH`, задайте в настройках нового окна:

```json
{
	"kern.lsp.command": "C:/Users/ILNAR/Desktop/KernDS/src/Kern.Cli/bin/Release/net10.0/kernc.exe",
	"kern.lsp.args": ["lsp"]
}
```

На Linux/macOS путь заканчивается на `.../net10.0/kernc`.

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

- диагностика компилятора (`KRN…`) при открытии/изменении файла;
- автодополнение ключевых слов и идентификаторов файла;
- hover — строка под курсором;
- переход к определению `fun`/`type`/`unit` по имени;
- символы документа (функции, типы, unit, глобалы);
- форматирование (табы/CRLF, хвостовые пробелы, как `kernc fmt`).

## Ограничения MVP

- hover/переход к определению синтаксические, не типовые;
- нет references/rename/semantic tokens/signature help/code actions;
- внешние `Kern.Lib.*` библиотеки в LSP пока не загружаются, поэтому `use kern.db[...]`
  может показывать неразрешённые имена;
- форматтер консервативный (не pretty-printer).
