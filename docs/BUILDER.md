# BUILDER — the Gradle-like Kern build system (replaces `kern.toml`)

Status: owner decisions are marked **[FINAL]**; API details marked **[PROPOSED]** are the default you implement unless it proves wrong —
then write the problem + options to `DECISIONS_NEEDED.md` (do not silently change it).

## 1. Principles
- **[FINAL]** `kern.toml` does not exist. Project configuration is ordinary Kern code in `build.kern` (like `build.gradle.kts`), type-checked by the same compiler.
- **[FINAL]** Typed API: `use kern.build[workspace, target, profile]`, `fun build(workspace w)`, `w.target("app", fun (target t) { … })`. No new syntax in the language for the builder. Callback lambdas are ordinary positional arguments inside the call parentheses.
  (Use `use kern.build[…]`, never bare `use kern.build` — it creates an alias `build` that clashes with `fun build`.)
- **[FINAL]** `build.kern` is optional: a plain folder works by convention.
- `build.kern` is executed by the VM at build time (it can run arbitrary code — document this in `kernc help build`).

## 2. Convention (no build.kern) [FINAL]
- `kernc run file.kern` — compile that single file (+ std) and run its `main`.
- `kernc run <dir>` / `kernc build <dir>`: sources = `<dir>/src/**/*.kern`; if there is no `src/`, `<dir>/*.kern`.
  Entry = module `main` (`main.kern`, containing `fun main`). Project name = folder name. Kind = `app` if a `main` exists, otherwise `lib`.
- Module name = path relative to the source dir without extension, dots for separators (`geometry/shapes.kern` → `geometry.shapes`), or an explicit first line `project a.b:`.
- Outputs: `<dir>/out/<name>.kbc` (`build`, `dll`; for a library the same file has no entry point and embedded
  `kern.stub` interface sources, so `t.kbc(...)` works without a separate `dll` command), `<dir>/dist/<name>[.exe]` (`exe`),
  `<dir>/dist/<name>/` (`dist`: host + `program.kbc` + `libs/`). Source paths inside `.kbc` are relative to the project dir.
- Project kinds are the C# ones: `app` (aliases `exe`, `console`) is executable, `lib` (aliases `library`, `classlib`)
  is a library; `kernc new` accepts both spellings and writes `t.kind` explicitly.

## 3. `build.kern` shape [PROPOSED, based on the working old design]
```kern
use kern.build[workspace, target, profile]

fun build(workspace w) {
	w.name = "demo"
	w.version = "1.0.0"

	w.include("libs/mathx")              // sub-project: its own build.kern or convention
	w.lib_path("libs")                   // extra search path for external Kern.Lib.*.dll

	w.compiler.optimize = 1              // 0 | 1 | 2
	w.compiler.warnings_as_errors = false
	w.compiler.macros = true

	w.profile("release", fun (profile p) {
		p.optimize = 2
		p.env["MODE"] = "prod"           // env vars visible to the program (env.get) and to tasks
	})

	w.target("core", fun (target t) {
		t.kind = "lib"                   // lib: sources join dependants (source-level dependency)
	})

	w.target("app", fun (target t) {
		t.kind = "app"                   // default: app if main exists
		t.src = ["src"]                  // default ["src"]
		t.entry = "main"                 // module with fun main
		t.deps = ["core"]                // source-level deps on other targets (same workspace or "project:target")
		t.kbc("../mathx/out/mathx.kbc")  // binary dep through interface stubs + linker
		t.lib("kern.db")                 // external DLL library by module name (must be discoverable)
		t.args = ["--verbose"]           // default program args for `run`
		t.task("hello", fun println("привет из цели app"))
	})

	w.task("release", ["app:exe"], fun println("релиз собран"))
}
```
`t.deps` on a `lib` target compiles its modules together with the dependant (old behaviour). Optional later: `t.link = "kbc"` to build the dependency
as `.kbc` and link through stubs (ask owner before adding).

## 4. API surface to implement (module `kern.build`, C# in `Kern.Runtime/Build` + CLI side in `Kern.Cli/Build`)
- `workspace`: `name`, `version`, `compiler` (`optimize`, `warnings_as_errors`, `macros`, `debug_info`), `include(path)`, `lib_path(path)`, `profile(name, fun)`, `target(name, fun)`, `task(name, after, fun)`, `env(name) -> str?` (process env), `root` (absolute path).
- `target`: `name`, `kind`, `src`, `entry`, `deps`, `kbc(path)`, `lib(module)`, `lib_path(path)`, `args`, `env` (dict), `task(name, fun)`, per-target `compiler` override.
- `profile`: `optimize`, `warnings_as_errors`, `env` dict; built-in profiles `debug` (default, `-O1`) and `release` (`-O2`).
- Built-in tasks per target: `compile`, `run`, `exe`, `dist`, `dll`, `check`, `dis`, `clean`. Workspace: `build` (all targets), `clean`, plus user tasks. (No `test` task in stage 1.)
- **Up-to-date check:** skip recompilation when the hash of sources + dependency targets + `.kbc` deps + compiler settings + profile + kernc version is unchanged (the old implementation forgot `.kbc` deps in the hash - don't).
- **Native output:** `kernc build --native [--target <rid>]` builds `.kbc` and publishes it *inside* an executable. Compatible programs use **NativeAOT** (`PublishAot`, VM + std + bytecode, no installed .NET); generic natives, external `Kern.Lib.*` DLLs, `use dotnet`/reflection and a foreign RID fall back to a self-contained single-file host with the reason printed to stderr. Result `<project>/dist/native/<name>[.exe]`; `--target linux-x64|win-x64|osx-arm64` selects the RID.
- Task ordering: `after` list = prerequisite tasks (topological, cycle → clear error).

## 5. Multi-project [FINAL: wanted] [PROPOSED shape]
`w.include("path")` loads `path/build.kern` (or convention) as a child workspace. Task/target ids: `target:task` in the current project;
`<project-path>:target:task` (project path relative to the root, `/` separators) from the root. `kernc build` at the root builds everything reachable; `kernc build libs/mathx` builds one project.

## 6. Library discovery (external `Kern.Lib.*.dll`) [FINAL: no toml]
Search order: env `KERN_LIBS` (`;` on Windows, `:` elsewhere) → `libs` next to `kernc` → `libs/_out` upward from kernc → `~/.kern/libs` → project `libs` →
`w.lib_path(...)` / `t.lib_path(...)`. A root is a folder (DLLs in it or in first-level subfolders) or a `.dll` itself. Dependencies (e.g. `Npgsql.dll`) are resolved next to the library.
(External libraries are implemented in `libs/`; discovery + `t.lib("name")` load them into the native catalogue.)

## 7. Profiles and environment [FINAL: wanted]
`kernc build -p release`, `kernc run -p release …`. `p.env[...]`/`t.env[...]` are exported to the program's process env for `run` and to tasks.
Reading the real process environment inside `build.kern`: `w.env("NAME")` or `env.get("NAME")`.

## 7a. Compiler settings [FINAL: wanted]
Optimization level, warnings-as-errors, macros on/off, debug info (source line tables). Defaults: `-O1`, warnings not errors, macros on.

## 8. Packages by name [FINAL: implemented 2026-11-03]
1. Local first: `w.include(path)`, `t.kbc(path)`, `t.lib(name)`.
2. Minimal remote packages: `t.pkg("name", url: "https://…", sha256: "…")` downloads a zip (a project with `build.kern` or a `.kbc`) to `~/.kern/pkgs/<sha256>/`, **verifies the hash**, then treats it as `include`/`kbc`. No registry, no version solving.
   - Сеть: только HTTPS (каждый шаг ручных перенаправлений, не более 5), SHA-256 обязателен, распаковка защищена от zip-slip, кэш помечается `.complete`, единственная корневая папка архива разворачивается.
   - **Манифест и подпись (2026-11-03):** если в корне пакета есть `kern-package.json` (`name`, `version`, `description`, `permissions`), имя обязано совпадать с `t.pkg`, версия обязательна, а рядом должен лежать `kern-package.sig` — Base64 RSA PKCS#1 SHA-256 по точным байтам манифеста. Открытый ключ задаётся `KERN_PACKAGE_PUBLIC_KEY` (PEM) или `KERN_PACKAGE_KEY_FILE`; без ключа/подписи пакет с манифестом отклоняется. Пакеты без манифеста остаются совместимыми (только SHA-256). `permissions` — метаданные для будущей политики capabilities.
   - Проект с `build.kern` открывается как `w.include` и подключается зависимостью `<имя-пакета>:<цель>`; цель выбирается так: единственная, затем с именем пакета, затем единственная `lib`-цель.
   - `.kbc` (в корне или `out/`) добавляется в `t.kbc`.
   - `KERN_HOME` меняет корень кэша (`<KERN_HOME>/pkgs`), по умолчанию `~/.kern/pkgs`.
3. Wrapper-like pinned launcher: `kernw` / `kernw.cmd` + a `kern-version` text file, downloads and runs the pinned `kernc`.
   - `kern-version`: первая строка — версия (можно `v0.1.0`), опционально `sha256-<rid>=<hex>` или `sha256=<hex>`; при наличии контрольная сумма архива проверяется.
   - Кэш: `<KERN_HOME>/dist/<версия>/kern-<rid>/`, архив берётся из GitHub Releases (`SaihonFox/Kern`, тег `v<версия>`). `KERNW_BASE_URL` переопределяет базу (зеркала/тесты); HTTP разрешён только при `KERNW_ALLOW_HTTP=1`.
   - `kernw` (sh) и `kernw.cmd` (PowerShell) передают все аргументы `kernc`; сеть только HTTPS.
Network code needs a security review (hash mandatory, https only, no redirects to other hosts without hash match).

## 9. Open points (put the answer you choose + reason into `DECISIONS_NEEDED.md`, do not block)
- Exact task-id syntax for multi-project (above is a proposal).
- Whether `kernc run <dir>` without `build.kern` should accept `--entry module`.
- Whether a `lib` target also gets `dll` output by default.
