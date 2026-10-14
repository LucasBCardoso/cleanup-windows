<#
.SYNOPSIS
  Builds Cleanup-Setup-<version>.exe (Inno Setup 6, installer/Cleanup.iss) from a published folder. publish.ps1 runs it
  at the end; the release runs it on its own, after SignPath has signed the binaries in the folder.

.EXAMPLE
  ./scripts/setup.ps1 -Version 1.3.4                 # artifacts\Cleanup → artifacts\Cleanup-Setup-1.3.4.exe
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Source = (Join-Path $PSScriptRoot '..\artifacts\Cleanup'),
    [string]$Out = (Join-Path $PSScriptRoot '..\artifacts')
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

# Inno Setup 6: `winget install JRSoftware.InnoSetup`; preinstalled on GitHub's Windows runners.
$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    Write-Warning "Inno Setup not found: no Setup.exe (winget install JRSoftware.InnoSetup)"
    return
}

& $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$((Resolve-Path $Source).Path)" "/DOutputDir=$((Resolve-Path $Out).Path)" (Join-Path $root 'installer\Cleanup.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
Join-Path $Out "Cleanup-Setup-$Version.exe"
