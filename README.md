# Cleanup for Windows

The Windows version of [Cleanup](https://github.com/LucasBCardoso/cleanup), the CleanMyMac-style cleaner written in
Swift for macOS. Same modules, same safety model, same Daybreak / Ledger look, built natively with
**C# / .NET 10 + WinUI 3** for Windows 10 22H2 and Windows 11.

The macOS repo is the reference implementation. Everything here follows its design document
[`docs/superpowers/specs/2026-10-12-windows-version-design.md`](https://github.com/LucasBCardoso/cleanup/blob/master/docs/superpowers/specs/2026-10-12-windows-version-design.md):
§1 is the feature checklist and its invariants, §4–§8 map each part to Windows, §10 is the roadmap.

## Status

| Phase | Deliverable | State |
|---|---|---|
| **W0 · Setup** | Solution layout, CI, vendored `shared/` (design tokens, test vectors, `rules/windows.json`), version comparator, path containment, rule schema, XAML theme tokens | done |
| W1 · Core + CLI | rules, scan engine, safety, Recycle Bin cleaner, large files, Space Lens, ignore list, search/selection, history + undo; `cleanup.exe` | next |
| W2 · App shell | WinUI app: themes, top tabs, Hoje, Smart Scan, Arquivos grandes, Space Lens | |
| W3–W9 | Uninstaller, Updater, Duplicados, Desenvolvedor, elevation, Otimização, tray + health, Privacidade, links, packaging | |

## Layout

```
src/
  Cleanup.Core/             rules, scan engine, safety, history, health, updater logic — no Windows APIs
  Cleanup.Platform.Windows/ Recycle Bin, registry, MSIX, winget/Store, Authenticode, PDH/WMI, startup items
  Cleanup.Elevated/         UAC helper: named-pipe server with an operation allow-list (W6)
  Cleanup.Cli/              cleanup.exe — same commands and flags as the macOS CLI (W1)
  Cleanup.App/Themes/       Daybreak / Ledger / Tokens XAML resource dictionaries (the WinUI project arrives in W2)
tests/
  Cleanup.Core.Tests/       xUnit; runs shared/test-vectors, checks the XAML themes against the design tokens
  Cleanup.Platform.Tests/   integration tests on windows-latest
shared/                     vendored from the macOS repo — do not edit here
scripts/sync-shared.ps1     re-vendors shared/ and records the commit + SHA-256 of every file in shared/SOURCE
```

## Shared assets

`shared/` in the macOS repo is the source of truth; both test suites run the same files. To change a vector, a rule
or a token: edit it in the macOS repo, commit, make `swift test` pass there, then here:

```powershell
./scripts/sync-shared.ps1                          # from ../cleanup at HEAD
./scripts/sync-shared.ps1 -MacRepo D:\src\cleanup -Ref master
```

`SharedSourceTests` fails if any file under `shared/` differs from the hashes in `shared/SOURCE`.

Vectors whose Windows implementation comes later (search, selection, keep rule, privileged batch, health,
autoclean, deeplinks, disk space) are only checked for shape until their phase wires them to real code.

## Build and test

Requires the .NET 10 SDK (pinned in `global.json`). The WinUI app (W2) will also need Visual Studio 2022+ with the
*WinUI application development* workload.

```powershell
dotnet build Cleanup.sln
dotnet test Cleanup.sln
```

## Safety model

The same invariants as macOS (design §1): Recycle Bin by default; a safety policy re-checks every path at clean
time; never follow junctions or symlinks; elevation only deletes direct children of allow-listed folders, with one
UAC prompt per action; restore never overwrites; background work never elevates; links never delete.
