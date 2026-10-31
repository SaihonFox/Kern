---
description: "Implements kernc CLI, project model, build.kern builder, kbc stubs/linker, macros pipeline, fmt, repl"
mode: subagent
model: deepseek-v4-flash
temperature: 0.1
steps: 80
permission:
  edit: allow
  bash:
    "*": ask
    "dotnet build*": allow
    "dotnet test*": allow
    "dotnet run*": allow
    "dotnet format*": allow
    "dotnet restore*": allow
    "dotnet new*": allow
    "dotnet sln*": allow
    "dotnet add*": allow
    "dotnet clean*": allow
    "dotnet --info": allow
    "node tools/*": allow
    "dir*": allow
    "ls*": allow
    "type *": allow
    "cat *": allow
    "grep *": allow
    "rg *": allow
    "findstr *": allow
    "mkdir *": allow
    "git *": deny
    "rm -rf*": deny
    "del /s*": deny
    "rmdir /s*": deny
    "Remove-Item*-Recurse*": deny
---
You implement **kernc** (`Kern.Cli`): commands `run, check, build, dis, exe, dll, fmt, repl, clean, tasks, do, help, version`; the project model by convention (`src/` + `main.kern`, **no kern.toml**); the Gradle-like **`build.kern` builder** (see `docs/BUILDER.md`); `.kbc` through `use` (interface stubs + linker, see spec "Раунд 25"); macro expansion pipeline; library discovery; UTF-8 console.
There is NO test-runner command in stage 1. Read `docs/BUILDER.md` and `docs/DECISIONS.md` first. Builder questions not covered there → `DECISIONS_NEEDED.md`.
Build + test before reporting; report files changed, results, open questions.
