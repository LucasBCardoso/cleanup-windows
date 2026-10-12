<#
.SYNOPSIS
  Builds a self-contained Cleanup for Windows (design §10 W9): Cleanup.exe (app), cleanup.exe (CLI) and
  Cleanup.Elevated.exe (UAC helper) in one folder, plus a zip next to it. No .NET or Windows App SDK install needed.

.EXAMPLE
  ./scripts/publish.ps1                 # → artifacts/Cleanup and artifacts/Cleanup-win-x64.zip
  ./scripts/publish.ps1 -Version 1.2.0
#>
param(
    [string]$Version = '1.0.0',
    [string]$Runtime = 'win-x64',
    [string]$Out = (Join-Path $PSScriptRoot '..\artifacts')
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$target = Join-Path $Out 'Cleanup'
if (Test-Path $target) { Remove-Item -Recurse -Force $target }
New-Item -ItemType Directory -Force $target | Out-Null

$common = @('-c', 'Release', '-r', $Runtime, '--self-contained', 'true', "-p:Version=$Version", '-nologo', '-v', 'q')
foreach ($project in 'src\Cleanup.App\Cleanup.App.csproj', 'src\Cleanup.Cli\Cleanup.Cli.csproj', 'src\Cleanup.Elevated\Cleanup.Elevated.csproj') {
    Write-Host "Publishing $project"
    dotnet publish (Join-Path $root $project) @common -o $target
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $project" }
}

foreach ($exe in 'Cleanup.exe', 'cleanup-cli.exe', 'Cleanup.Elevated.exe', 'Cleanup.pri') {
    if (-not (Test-Path (Join-Path $target $exe))) { throw "missing $exe in $target" }
}
# `cleanup` in a terminal (after install.ps1 -AddToPath): a shim to the CLI.
Set-Content -Path (Join-Path $target 'cleanup.cmd') -Value '@"%~dp0cleanup-cli.exe" %*' -Encoding ascii
Copy-Item (Join-Path $PSScriptRoot 'install.ps1') $target
$zip = Join-Path $Out "Cleanup-$Runtime.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $target '*') -DestinationPath $zip
Write-Host "Done: $target"
Write-Host "      $zip"
