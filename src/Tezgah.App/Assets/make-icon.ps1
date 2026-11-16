# Regenerates tezgah.ico and docs/logo.svg from the single logo definition in src/Tezgah.App/Views/Logo.cs (LogoArt).
#
# Every icon size (256, 128, 64, 48, 40, 32, 24, 20, 16) is rendered separately by WPF from the vector, with each edge
# snapped to a whole pixel for that size - never downscaled from one big bitmap. 64 px and up are PNG entries, smaller
# ones 32-bit DIBs. Run from any folder (Windows, .NET 10 SDK):
#   pwsh -File src/Tezgah.App/Assets/make-icon.ps1
# Optional: -Preview <folder> also keeps logo-<size>.png renders there for inspection.
param([string]$Preview)

$ErrorActionPreference = 'Stop'
$app = Split-Path $PSScriptRoot -Parent
$repo = Split-Path (Split-Path $app -Parent) -Parent
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("tezgah-logo-" + [guid]::NewGuid().ToString('N'))

# The --export-logo switch exists only in Debug builds.
dotnet build (Join-Path $app 'Tezgah.App.csproj') -c Debug -v q -nologo
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$exe = Join-Path $app 'bin/Debug/net10.0-windows/Tezgah.exe'
$p = Start-Process -FilePath $exe -ArgumentList '--export-logo', $work -PassThru
if (-not $p.WaitForExit(60000)) { $p.Kill(); throw 'export timed out' }
if ($p.ExitCode -ne 0) { throw "export failed ($($p.ExitCode))" }

Copy-Item (Join-Path $work 'tezgah.ico') (Join-Path $PSScriptRoot 'tezgah.ico') -Force
Copy-Item (Join-Path $work 'logo.svg') (Join-Path $repo 'docs/logo.svg') -Force
if ($Preview) {
    New-Item -ItemType Directory -Force $Preview | Out-Null
    Copy-Item (Join-Path $work 'logo-*.png') $Preview -Force
}
Remove-Item $work -Recurse -Force
Write-Host 'wrote' (Join-Path $PSScriptRoot 'tezgah.ico') 'and docs/logo.svg'
Write-Host 'Rebuild the app so the new icon is embedded.'
