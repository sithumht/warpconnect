# Build WARP Connect for Windows — engine + native shell.
#
# Requires: Go 1.24+ on PATH (or $env:GOEXE), Windows with .NET Framework 4.x
# (present on Windows 10+), and no network (the app icon is built from the
# committed raster under docs/brand/).
#
# Output: windows\bin\WARP Connect\  (warpscout.exe + wintun.dll placeholder +
# WARP Connect.exe + LICENSE). WinTUN itself is downloaded on first run.

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $PSScriptRoot "bin\WARP Connect"
$bin = Join-Path $PSScriptRoot "bin"

Write-Output "== Clean =="
Remove-Item -Recurse -Force $outDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
New-Item -ItemType Directory -Force -Path $bin | Out-Null

Write-Output "== Build engine (windows/amd64) =="
$go = if ($env:GOEXE) { $env:GOEXE } elseif (Get-Command go -ErrorAction SilentlyContinue) { "go" } elseif (Test-Path "$env:USERPROFILE\go\bin\go.exe") { "$env:USERPROFILE\go\bin\go.exe" } elseif (Test-Path "$env:USERPROFILE\AppData\Local\Programs\Go\bin\go.exe") { "$env:USERPROFILE\AppData\Local\Programs\Go\bin\go.exe" } else { throw "Go not found on PATH or default locations" }
$env:GOOS = "windows"; $env:GOARCH = "amd64"; $env:CGO_ENABLED = "0"
Push-Location (Join-Path $repo "engine")
& $go build -o (Join-Path $outDir "warpscout.exe") -ldflags "-s -w" .
if ($LASTEXITCODE -ne 0) { throw "engine build failed" }
Pop-Location

Write-Output "== Generate icon (PNG -> ICO) =="
Add-Type -AssemblyName System.Drawing
$src = Join-Path $repo "docs\brand\warpconnect-icon-256.png"
$ico = Join-Path $bin "warpconnect.ico"
$png = [System.Drawing.Bitmap]::FromFile($src)
try {
    $h = $png.GetHicon()
    $icon = [System.Drawing.Icon]::FromHandle($h)
    $fs = [System.IO.File]::Create($ico)
    $icon.Save($fs)
    $fs.Dispose()
    $icon.Dispose()
    [System.Runtime.InteropServices.Marshal]::Release($h) | Out-Null
} finally { $png.Dispose() }

Write-Output "== Compile app (csc) =="
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$csc = Join-Path $fw "csc.exe"
$app = Join-Path $PSScriptRoot "src"
$refs = @(
    Join-Path $fw "System.Windows.Forms.dll"
    Join-Path $fw "System.Drawing.dll"
    Join-Path $fw "System.Core.dll"
    Join-Path $fw "System.Net.dll"
    Join-Path $fw "System.Xml.dll"
    Join-Path $fw "System.Web.Extensions.dll"
    Join-Path $fw "System.Runtime.Serialization.dll"
    Join-Path $fw "System.IO.Compression.dll"
    Join-Path $fw "System.IO.Compression.FileSystem.dll"
    Join-Path $fw "Microsoft.CSharp.dll"
)
$args2 = @(
    "/nologo", "/target:winexe", "/optimize+",
    "/out:$outDir\WARP Connect.exe",
    "/win32manifest:$PSScriptRoot\app.manifest",
    "/win32icon:$ico"
) + ($refs | ForEach-Object { "/reference:$_" }) + (Get-ChildItem $app -Filter *.cs | ForEach-Object { $_.FullName })
& $csc @args2
if ($LASTEXITCODE -ne 0) { throw "app compile failed" }

Write-Output "== Copy docs =="
Copy-Item (Join-Path $repo "LICENSE") (Join-Path $outDir "LICENSE.txt")
Copy-Item (Join-Path $PSScriptRoot "README.md") (Join-Path $outDir "README.md")

Write-Output ""
Write-Output "Built: $outDir"
Write-Output "Run:   $outDir\WARP Connect.exe  (UAC will prompt for Administrator)"
Write-Output ""
Write-Output "First connect can take 1-3 minutes on a filtered network."
