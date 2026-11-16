# Build WARP Connect for Windows (WPF front-end) using .NET 8 portable SDK.
#
# Requires: Windows 10/11 with .NET 8 SDK on PATH (or dotnet-portable install),
# Go 1.24+ on PATH (or $GOEXE), and the wintun.dll (fetched at first run, GPLv2).
#
# Output: windows\warp-connect-wpf\bin\Release\ ; warpscout.exe is copied into the output.

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$outDir = Join-Path $PSScriptRoot "bin\Release"

Write-Output "== Clean =="
Remove-Item -Recurse -Force $outDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Output "== Build engine (windows/amd64) =="
$go = if ($env:GOEXE) { $env:GOEXE } elseif (Get-Command go -ErrorAction SilentlyContinue) { "go" } elseif (Test-Path "$env:USERPROFILE\AppData\Local\Programs\Go\bin\go.exe") { "$env:USERPROFILE\AppData\Local\Programs\Go\bin\go.exe" } else { throw "Go not found on PATH or default locations" }
$env:GOOS = "windows"; $env:GOARCH = "amd64"; $env:CGO_ENABLED = "0"
Push-Location (Join-Path $repo "engine")
& $go build -o (Join-Path $outDir "warpscout.exe") -ldflags "-s -w" .
if ($LASTEXITCODE -ne 0) { throw "engine build failed" }
Pop-Location

Write-Output "== Copy engine into WPF project =="
Copy-Item (Join-Path $outDir "warpscout.exe") (Join-Path $PSScriptRoot "warpscout.exe") -Force

Write-Output "== dotnet publish =="
$portableSdk = "$env:USERPROFILE\dotnet-portable\dotnet.exe"
$sdk = if ($env:DOTNET) { $env:DOTNET } elseif (Test-Path $portableSdk) { $portableSdk } elseif (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" } else { throw "dotnet SDK not found; install .NET 8 SDK or set DOTNET=C:\path\to\dotnet.exe" }
& $sdk publish -c Release -o $outDir $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw "WPF publish failed" }

Write-Output ""
Write-Output "Built: $outDir"
Write-Output "Run:   $outDir\WARP Connect.exe (UAC will prompt for Administrator)"
Write-Output ""
Write-Output "WinTUN driver is downloaded on first run from wintun.net."
