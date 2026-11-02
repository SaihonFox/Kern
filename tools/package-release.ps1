<#
.SYNOPSIS
	Собрать переносимый дистрибутив Kern (kernc + библиотеки + примеры).

.DESCRIPTION
	Публикует kernc одним self-contained файлом (по умолчанию с trimming),
	кладёт рядом каталог libs/ с Kern.Lib.* и копирует examples/.
	Полученную папку (или zip) можно отдать другому человеку: kernc сам
	находит библиотеки в libs/ рядом с собой.

.PARAMETER Rid
	Runtime identifier: win-x64, linux-x64, osx-arm64, ...

.PARAMETER NoTrim
	Не обрезать неиспользуемый код (PublishTrimmed=false). Полезно, если
	внешняя библиотека использует нестандартную рефлексию.

.PARAMETER NoZip
	Не создавать zip-архив.
#>
param(
	[string]$Rid = "win-x64",
	[string]$Configuration = "Release",
	[switch]$NoTrim,
	[switch]$NoZip
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$name = "kern-$Rid"
$dist = Join-Path $root "dist"
$out = Join-Path $dist $name

if (Test-Path $out)
{
	Remove-Item $out -Recurse -Force
}

New-Item -ItemType Directory -Force $out | Out-Null

Write-Host "Сборка библиотек ($Configuration)..."
dotnet build (Join-Path $root "libs/Kern.Libs.slnx") -c $Configuration
if ($LASTEXITCODE -ne 0)
{
	throw "Сборка libs/Kern.Libs.slnx не удалась"
}

$trim = if ($NoTrim) { "false" } else { "true" }
Write-Host "Публикация kernc ($Rid, Trimmed=$trim)..."
dotnet publish (Join-Path $root "src/Kern.Cli/Kern.Cli.csproj") `
	-c $Configuration `
	-r $Rid `
	--self-contained true `
	-p:PublishSingleFile=true `
	-p:PublishTrimmed=$trim `
	-p:TrimMode=full `
	-p:EnableCompressionInSingleFile=false `
	-o $out
if ($LASTEXITCODE -ne 0)
{
	throw "Публикация kernc не удалась"
}

$libs = Join-Path $out "libs"
New-Item -ItemType Directory -Force $libs | Out-Null
Copy-Item (Join-Path $root "libs/_out/*") $libs -Recurse -Force

Copy-Item (Join-Path $root "examples") (Join-Path $out "examples") -Recurse -Force
Get-ChildItem (Join-Path $out "examples") -Recurse -Directory |
	Where-Object { $_.Name -in @("bin", "obj", "out") } |
	Remove-Item -Recurse -Force
Get-ChildItem (Join-Path $out "examples") -Recurse -File -Filter "*.db" |
	Remove-Item -Force

$readme = @"
Kern — компилятор и VM.

Запуск примера:
    kernc.exe run examples/01_basics
    kernc.exe run examples/02_types
    kernc.exe check examples/27_backend

Библиотеки (kern.db, kern.backend, kern.di, kern.ffi, kern.transpile)
лежат в каталоге libs/ рядом с kernc.exe и находятся автоматически.
Примеры с build.kern подключают их через t.lib("kern.backend").

Переменная KERN_LIBS может задать дополнительные каталоги библиотек
(через ; на Windows и : на Linux/macOS).
"@
Set-Content -Path (Join-Path $out "README.txt") -Value $readme -Encoding utf8

if (-not $NoZip)
{
	$zip = Join-Path $dist "$name.zip"
	if (Test-Path $zip)
	{
		Remove-Item $zip -Force
	}

	Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -Force
	Write-Host "Готово: $zip"
}
else
{
	Write-Host "Готово: $out"
}
