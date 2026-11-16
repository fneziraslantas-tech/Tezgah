<#
.SYNOPSIS
  Fails unless the published Tezgah.exe carries the expected version.
.DESCRIPTION
  The About dialog shows the assembly's InformationalVersion without its "+<commit>" suffix. For the single-file
  exe the same value is the ProductVersion of its Win32 version resource, so this checks what users will see.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Expected
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw "$Exe was not produced" }

$info = (Get-Item -LiteralPath $Exe).VersionInfo
$product = ($info.ProductVersion -split '\+', 2)[0]
Write-Output "ProductVersion: $($info.ProductVersion) / FileVersion: $($info.FileVersion)"
if ($product -cne $Expected) { throw "Tezgah.exe reports version '$product', expected '$Expected'" }
if ($info.FileVersion -cne "$Expected.0") { throw "Tezgah.exe file version is '$($info.FileVersion)', expected '$Expected.0'" }
