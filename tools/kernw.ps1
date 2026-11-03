# kernw — запускает kernc закреплённой версии из kern-version (docs/BUILDER.md §8).
# Скачивает официальный архив GitHub Releases в %KERN_HOME%\.kern\dist\<версия>\kern-<rid>\
# и передаёт все аргументы kernc. Пакеты кода (t.pkg) проверяются отдельно по SHA-256.
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$versionFile = Join-Path $root "kern-version"
if (-not (Test-Path -LiteralPath $versionFile)) {
    throw "kernw: не найден $versionFile"
}

# Матрица релиза содержит win-x64; на Windows ARM64 используется x64-эмуляция.
$rid = "win-x64"

$version = ""
$shaAny = ""
$shaRid = ""
foreach ($raw in Get-Content -LiteralPath $versionFile) {
    $line = $raw.Trim()
    if ($line.Length -eq 0 -or $line.StartsWith("#")) { continue }
    $equals = $line.IndexOf("=")
    if ($equals -lt 0) {
        if ($version.Length -eq 0) { $version = $line }
        continue
    }
    $key = $line.Substring(0, $equals).Trim().ToLowerInvariant()
    $value = $line.Substring($equals + 1).Trim()
    if ($key -eq "version") { $version = $value }
    elseif ($key -eq "sha256") { $shaAny = $value }
    elseif ($key -eq "sha256-$rid") { $shaRid = $value }
}
if ($version.StartsWith("v")) { $version = $version.Substring(1) }
if ($version.Length -eq 0) { throw "kernw: в kern-version не указана версия" }

$homeDir = if ($env:KERN_HOME) { $env:KERN_HOME } else { Join-Path $env:USERPROFILE ".kern" }
$cache = Join-Path $homeDir "dist\$version\kern-$rid"
$exe = Join-Path $cache "kernc.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    $base = if ($env:KERNW_BASE_URL) { $env:KERNW_BASE_URL.TrimEnd("/") } else { "https://github.com/SaihonFox/Kern/releases/download" }
    $url = "$base/v$version/kern-$rid.zip"
    if ($url -notmatch "^https://") {
        if ($url -notmatch "^http://" -or $env:KERNW_ALLOW_HTTP -ne "1") {
            throw "kernw: http разрешён только при KERNW_ALLOW_HTTP=1 (для локальных тестов/зеркал)"
        }
    }
    $zip = Join-Path ([System.IO.Path]::GetTempPath()) ("kernw-" + [guid]::NewGuid().ToString("N") + ".zip")
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $zip
        $expected = if ($shaRid) { $shaRid } else { $shaAny }
        if ($expected) {
            $stream = [System.IO.File]::OpenRead($zip)
            try {
                $sha = [System.Security.Cryptography.SHA256]::Create()
                try { $bytes = $sha.ComputeHash($stream) }
                finally { $sha.Dispose() }
            }
            finally { $stream.Dispose() }
            $actual = ([System.BitConverter]::ToString($bytes) -replace "-", "").ToLowerInvariant()
            if ($actual -ne $expected.ToLowerInvariant()) {
                throw "kernw: SHA-256 не совпадает (ожидался $expected, получен $actual)"
            }
        }
        Expand-Archive -LiteralPath $zip -DestinationPath $cache -Force
    }
    finally {
        Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    }
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "kernw: архив не содержит kernc ($url)"
    }
}

& $exe @args
exit $LASTEXITCODE
