<#
.SYNOPSIS
  Copies shared/ from the macOS repo (the source of truth) into this repo and records its commit and file hashes
  in shared/SOURCE. SharedSourceTests fails if any vendored file no longer matches those hashes.

.DESCRIPTION
  Files are taken from a commit of the macOS repo (git archive), never from its working tree, so the bytes are
  exactly what is committed there: no CRLF conversion, no uncommitted edits.

.EXAMPLE
  ./scripts/sync-shared.ps1                                  # ../cleanup at HEAD
  ./scripts/sync-shared.ps1 -MacRepo D:\src\cleanup -Ref master
#>
param(
    [string]$MacRepo = (Join-Path $PSScriptRoot '..\..\cleanup'),
    [string]$Ref = 'HEAD'
)

$ErrorActionPreference = 'Stop'
$MacRepo = (Resolve-Path $MacRepo).Path
$target = Join-Path $PSScriptRoot '..\shared'
$commit = (git -C $MacRepo rev-parse --verify "$Ref^{commit}").Trim()
if ($LASTEXITCODE -ne 0) { throw "Not a commit in ${MacRepo}: $Ref" }

$staging = Join-Path ([IO.Path]::GetTempPath()) ("cleanup-shared-" + [Guid]::NewGuid())
New-Item -ItemType Directory $staging | Out-Null
try {
    $tar = Join-Path $staging 'shared.tar'
    git -C $MacRepo archive --format=tar -o $tar $commit shared
    if ($LASTEXITCODE -ne 0) { throw "git archive failed" }
    tar -xf $tar -C $staging
    if ($LASTEXITCODE -ne 0) { throw "tar failed" }

    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    Move-Item (Join-Path $staging 'shared') $target

    $root = (Resolve-Path $target).Path
    $lines = @(
        "# Vendored from the macOS repo by scripts/sync-shared.ps1. Do not edit files here; change them there and sync.",
        "repo LucasBCardoso/cleanup",
        "commit $commit"
    )
    Get-ChildItem $root -Recurse -File | Sort-Object { $_.FullName.Substring($root.Length).Replace('\', '/') } -CaseSensitive | ForEach-Object {
        $relative = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $lines += "$hash  $relative"
    }
    [IO.File]::WriteAllText((Join-Path $root 'SOURCE'), ($lines -join "`n") + "`n")
    Write-Host "shared/ synced from $commit ($($lines.Count - 3) files)"
}
finally {
    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
}
