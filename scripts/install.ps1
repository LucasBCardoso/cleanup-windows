<#
.SYNOPSIS
  Installs Cleanup for the current user (no administrator needed): copies the published folder to
  %LOCALAPPDATA%\Programs\Cleanup, adds a Start Menu shortcut and, with -AddToPath, puts cleanup.exe on the user PATH.

.EXAMPLE
  .\install.ps1                  # from the published folder (artifacts\Cleanup or the unzipped release)
  .\install.ps1 -AddToPath
  .\install.ps1 -Uninstall       # removes the app, shortcut, login entry and cleanup: link (keeps your history)
#>
param(
    [string]$Source = $PSScriptRoot,
    [string]$Target = (Join-Path $env:LOCALAPPDATA 'Programs\Cleanup'),
    [switch]$AddToPath,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Cleanup.lnk'

function Stop-Cleanup {
    Get-Process -Name 'Cleanup' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Target, 'OrdinalIgnoreCase') } |
        ForEach-Object { $_.CloseMainWindow() | Out-Null; if (-not $_.WaitForExit(3000)) { $_.Kill() } }
}

function Set-UserPath([bool]$add) {
    $path = [Environment]::GetEnvironmentVariable('Path', 'User')
    $parts = @($path -split ';' | Where-Object { $_ -and ($_.TrimEnd('\') -ne $Target.TrimEnd('\')) })
    if ($add) { $parts += $Target }
    [Environment]::SetEnvironmentVariable('Path', ($parts -join ';'), 'User')
}

if ($Uninstall) {
    Stop-Cleanup
    if (Test-Path $shortcut) { Remove-Item $shortcut }
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Cleanup' -ErrorAction SilentlyContinue
    if (Test-Path 'HKCU:\Software\Classes\cleanup') { Remove-Item 'HKCU:\Software\Classes\cleanup' -Recurse }
    Set-UserPath $false
    if (Test-Path $Target) { Remove-Item -Recurse -Force $Target }
    Write-Host "Cleanup removido. Seu histórico e ajustes continuam em $env:LOCALAPPDATA\Cleanup."
    return
}

if (-not (Test-Path (Join-Path $Source 'Cleanup.exe'))) { throw "Cleanup.exe não encontrado em $Source (rode a partir da pasta publicada)." }
Stop-Cleanup
if ((Resolve-Path $Source).Path.TrimEnd('\') -ne $Target.TrimEnd('\')) {
    if (Test-Path $Target) { Remove-Item -Recurse -Force $Target }
    New-Item -ItemType Directory -Force $Target | Out-Null
    Copy-Item -Path (Join-Path $Source '*') -Destination $Target -Recurse -Force
}

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = Join-Path $Target 'Cleanup.exe'
$link.WorkingDirectory = $Target
$link.IconLocation = (Join-Path $Target 'Cleanup.exe') + ',0'
$link.Description = 'Limpe o PC com segurança'
$link.Save()

# A login entry or cleanup: link pointing at an older copy now points here (the app re-registers the link on launch).
if (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Cleanup' -ErrorAction SilentlyContinue) {
    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Cleanup' -Value ('"' + (Join-Path $Target 'Cleanup.exe') + '" --background')
}
if ($AddToPath) { Set-UserPath $true }

Write-Host "Cleanup instalado em $Target"
Write-Host "Menu Iniciar: Cleanup$(if ($AddToPath) { '   ·   Terminal: cleanup help (abra um novo terminal)' })"
Start-Process (Join-Path $Target 'Cleanup.exe')
