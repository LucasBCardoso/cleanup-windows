<#
.SYNOPSIS
  The version of this checkout: major.minor from the VERSION file, plus the number of commits since VERSION last
  changed (1.3 bumped three commits ago → 1.3.3). Every push to main publishes it as a release (release.yml), and
  the installed app updates itself to it. Without git (a zip) the third number is 0.
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$base = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
if ($base -notmatch '^\d+\.\d+$') { throw "VERSION must be major.minor (like 1.3), got '$base'" }
$count = 0
if (Get-Command git -ErrorAction SilentlyContinue) {
    $bump = git -C $root log -1 --format=%H -- VERSION 2>$null
    if ($LASTEXITCODE -eq 0 -and $bump) {
        $count = [int](git -C $root rev-list --count "$bump..HEAD")
    }
}
"$base.$count"
