# Distributing Kern to another machine

Kern has two parts at run time:

1. `kernc` — the compiler/VM (a single self-contained executable);
2. native libraries in `libs/` — `Kern.Lib.Db`, `Kern.Lib.Di`, `Kern.Lib.Backend`,
   `Kern.Lib.Ffi`, `Kern.Lib.Transpile` (each folder is self-contained, including
   third-party packages and `runtimes/<rid>/native`).

`kernc` finds libraries in this order (see `LibraryResolver`): `KERN_LIBS`,
`libs` next to `kernc`, `~/.kern/libs`, `libs` in the project, explicit `lib_path`.
For a friend, the simplest layout is therefore:

    kern-win-x64/
      kernc.exe
      README.txt
      libs/
        Kern.Lib.Db/
        Kern.Lib.Di/
        Kern.Lib.Backend/
        ...
      examples/
        01_basics/
        13_db_web/
        27_backend/
        ...

`kernc run examples/01_basics` works immediately; `kernc run examples/27_backend`
uses `libs/Kern.Lib.Backend` and `libs/Kern.Lib.Di` automatically.

## One-command packaging

From the repository root (Windows PowerShell):

    pwsh tools/package-release.ps1 -Rid win-x64

It builds `libs/Kern.Libs.slnx` in Release, publishes `kernc` as a trimmed
single-file executable, copies `libs/_out/*` into `libs/`, copies examples,
writes `README.txt` and produces `dist/kern-win-x64.zip`.

Useful options:

    -Rid linux-x64        # or osx-arm64, win-x86, ...
    -NoTrim               # PublishTrimmed=false (safest for unusual reflection)
    -NoZip                # leave an unpacked folder only

The recipient needs no .NET SDK/runtime: the executable is self-contained.
For Linux/macOS the `kernc` binary is not executable after unzip on some
systems; run `chmod +x kernc` once.

Release archives are built with `DebugType=None`/`DebugSymbols=false`, and
`tools/package-release.ps1` removes any `*.pdb` left by the SDK or libraries,
so the `.zip` contains no debug symbols.

## Trimming notes

Native modules/types are discovered by reflection over attributes
(`[KernModule]`, `[KernType]`, `[KernMember]`), and the optimizer is looked up by
type name. `Kern.Cli.csproj` therefore roots `Kern.Core`, `Kern.Bytecode`,
`Kern.Vm`, `Kern.Binding`, `Kern.Runtime`, `Kern.Compiler`, `Kern.Lsp` and `kernc` for the
trimmer, and `Properties/ILLink.Descriptors.xml` preserves the debugger
attributes used by runtime-loaded `Kern.Lib.*` assemblies. Without this,
trimmed builds compile programs with functions that have no bytecode
(`KRN2201`) or crash while reading custom attributes of a library.

If a third-party library relies on reflection that the trimmer cannot see, use
`-NoTrim` for the distribution.

## What to send

Send the whole `dist/kern-win-x64.zip` (or the folder). Do not send only
`kernc.exe`: programs that use `kern.db`/`kern.backend` need `libs/`, and
examples need `examples/` if you want them to be runnable out of the box.

## GitHub Releases

`.github/workflows/release.yml` builds `kernc` (self-contained, trimmed single-file) for
`win-x64`, `linux-x64`, `osx-x64` and `osx-arm64` on a tag `v*` (or manual dispatch). Each job
runs `tools/package-release.ps1`, uploads `kern-<rid>.zip` (kernc + `libs/` + `examples/`) and
the release job attaches all archives to the GitHub release with generated notes.

