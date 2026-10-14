# Cleanup for Windows

The Windows version of [Cleanup](https://github.com/LucasBCardoso/cleanup), the CleanMyMac-style cleaner first written
in Swift for macOS. It has the same modules, the same safety model and the same Daybreak / Ledger look, built natively
with **C# / .NET 10 + WinUI 3** for Windows 10 22H2 and Windows 11.

The macOS repo is the reference implementation. Everything here follows its design document,
[`docs/superpowers/specs/2026-10-12-windows-version-design.md`](https://github.com/LucasBCardoso/cleanup/blob/master/docs/superpowers/specs/2026-10-12-windows-version-design.md).

## Download and install

**[⬇ Baixar o Cleanup para Windows (Cleanup-Setup.exe)](https://github.com/LucasBCardoso/cleanup-windows/releases/latest/download/Cleanup-Setup.exe)**

1. Download the installer with the link above. It always points to the latest version. The
   **[Releases](https://github.com/LucasBCardoso/cleanup-windows/releases/latest)** page has the same file as
   `Cleanup-Setup-<version>.exe`, with its SHA-256 and what changed.
2. Run it. It needs Windows 10 22H2 or Windows 11 (x64); no administrator, .NET or Windows App SDK install is needed,
   because everything ships inside the installer.
3. Signed releases show **SignPath Foundation** as the publisher (see [Code signing policy](#code-signing-policy)).
   If SmartScreen still warns, which happens with an unsigned release or while a new certificate builds reputation,
   choose **Mais informações → Executar assim mesmo**.

The installer is for the current user only. It installs to `%LOCALAPPDATA%\Programs\Cleanup` and adds:

- a Start Menu shortcut (a Desktop shortcut is optional);
- an entry in **Configurações › Aplicativos**, which is also how you uninstall;
- optionally, `cleanup` on the PATH for the CLI.

Once installed, Cleanup **updates itself** (Ajustes › Atualizações, on by default). Every 6 hours it looks at the
latest release, downloads its `Cleanup-Setup-<version>.exe`, checks it against the SHA-256 GitHub publishes for the
file, and installs it silently when the window is closed (it comes back in the tray) or when you quit. A signed
Cleanup also requires the update to carry a valid signature from the same publisher, so a release swapped on GitHub
can't install itself. Only copies installed by the Setup update themselves; a development build or the zip never
does. Versions before 1.3 had no updater, so install 1.3 or later by hand once.

Installing a newer version over an older one keeps everything. Uninstalling removes the app, its login entry and the
`cleanup:` link, and keeps your history and settings in `%LOCALAPPDATA%\Cleanup`.

| Situation | Command |
|---|---|
| Silent install, e.g. in a script | `Cleanup-Setup-1.3.0.exe /VERYSILENT /SUPPRESSMSGBOXES` |
| Silent uninstall | `"%LOCALAPPDATA%\Programs\Cleanup\unins000.exe" /VERYSILENT` |

## Releasing a new version

Every push to `main` that changes the app is a release. GitHub Actions (`.github/workflows/release.yml`):

1. computes the version with `scripts/version.ps1`: major.minor from the `VERSION` file, plus the number of commits
   since `VERSION` last changed (`1.3` bumped five commits ago → `1.3.5`);
2. tests the solution, and stops there if anything fails;
3. builds the self-contained app (`scripts/publish.ps1 -NoSetup`);
4. **signing on:** sends Cleanup's own binaries to SignPath and waits for the approval (*first approval*);
5. builds `Cleanup-Setup-<version>.exe` from that folder (`scripts/setup.ps1`);
6. **signing on:** sends the Setup.exe to SignPath and waits again (*second approval*);
7. publishes the GitHub Release `v<version>` with the installer, a `.sha256` file and the commits since the previous
   release as notes. Installed copies pick it up within 6 hours.

Pushes that only touch Markdown or `docs/` don't release. To start a new minor or major line, change `VERSION`
(for example to `1.4`); that commit is released as `1.4.0`. The workflow can also be run by hand from **Actions ›
Release**. Pull requests are built and tested by `.github/workflows/ci.yml`, with the installer as the *Cleanup-Setup*
artifact of the run.

### Signing

Signing is on once the repository has the variable `SIGNPATH_ORGANIZATION_ID` and the secret `SIGNPATH_API_TOKEN`
(**Settings › Secrets and variables › Actions**). Without them, releases go out unsigned exactly as before. Each signed
release needs **two approvals on [signpath.io](https://app.signpath.io)**, a few minutes apart. Each request waits up
to 3 hours. If nobody approves in time, the run fails, nothing is published, and the next push (or **Re-run**) tries
again.

One-time setup, after SignPath Foundation accepts the project at [signpath.org/apply](https://signpath.org/apply):

1. In SignPath, create the project `cleanup-windows` with the signing policy `release-signing`. If you pick other
   slugs, set them in the variables `SIGNPATH_PROJECT_SLUG` and `SIGNPATH_POLICY_SLUG`.
2. Add two artifact configurations with the slugs `app` and `setup`, pasting `.signpath/app.xml` and
   `.signpath/setup.xml`. Keep the repo copies in sync with SignPath.
3. Link the predefined *GitHub.com* trusted build system to the project, and install the SignPath GitHub App on this
   repository.
4. Create an API token for a user who can submit to the policy. Store it as the secret `SIGNPATH_API_TOKEN`, and the
   organization ID as the variable `SIGNPATH_ORGANIZATION_ID`.

SignPath signs Cleanup's binaries only when their product name is `Cleanup` and their product version is the release
version, which `Directory.Build.props` sets. The Setup.exe is matched by its file name only (see `.signpath/setup.xml`).

### Building the installer locally

```powershell
winget install JRSoftware.InnoSetup           # once
./scripts/publish.ps1                         # → artifacts\Cleanup-Setup-<version>.exe, artifacts\Cleanup (folder) and a zip
./scripts/setup.ps1 -Version 1.3.4            # only the Setup.exe, from an existing artifacts\Cleanup
./artifacts/Cleanup/install.ps1 -AddToPath    # install without the Setup.exe (same folder; -Uninstall to remove)
```

Local builds are never signed.

The installer is defined in `installer/Cleanup.iss` (Inno Setup 6). Keep its `AppId` unchanged: Windows uses it to
recognise an upgrade of the same app.

## What's in it

| Tab | Module | What it does |
|---|---|---|
| **Hoje** | Hoje | Greeting, the **Espaço para liberar** hero with "Limpar agora" (safe items only, never a UAC prompt), and tiles for Disco, Lixeira, Saúde and Otimização total. The **Última limpeza** card has Desfazer. |
| **Limpeza** | Smart Scan | Rule-driven scan (`shared/rules/windows.json`). It covers temp files, crash dumps, error reports, the thumbnail cache, browser caches (these need review while the browser runs) and Electron/Chromium app caches, which are proven by their folder structure. It also covers shader caches, old installers in Downloads, and the Recycle Bin. System junk (Windows\Temp, the Windows Update cache, Delivery Optimization, system error reports, rotated logs) is removed with one UAC prompt. |
| | Arquivos grandes | Big files in your profile, optionally unused for N days (NTFS rarely records opens, so the last write counts too). |
| | Duplicados, Desenvolvedor | Identical files and similar photos; stale build output and developer caches. |
| | Privacidade | Browser history and cookies, recent items, PowerShell history, saved Wi-Fi networks. These are deleted permanently after a confirmation; the network in use is always kept. |
| **Apps** | Desinstalador, Atualizador, Extensões | Apps with their leftovers and a reset option; winget / Store updates; shell and browser extensions. |
| **Espaço** | Space Lens | What uses space, folder by folder. Junctions are listed, never followed. |
| **Sistema** | Otimização, Manutenção | Startup items and live CPU/memory use, Otimização total; flush DNS, free RAM, DISM, sfc, search index. |
| tray | Saúde | Notification-area icon and panel: CPU, memory pressure, disk, battery wear and uptime. Alerts can be snoozed and notify at most once every 6 h per alert. |
| Ajustes | | Appearance (Daybreak / Ledger / Automático), tray, notifications, launch at login, **Limpeza automática** (daily or weekly; notify or clean what is safe), permissions, and the introduction again. |

Links: `cleanup://scan`, `cleanup://history`, `cleanup://open/<module>` (links only navigate or start a read-only
scan). Shortcuts: Ctrl+1–5 for the tabs, Ctrl+R to scan, Ctrl+Y for the history.

## Safety model

These are the same invariants as on macOS (design §1):

- **Recycle Bin by default.** The location of every recycled item is recorded, so **Desfazer** puts it back. A restore
  never overwrites anything and only takes items out of your own `$Recycle.Bin\<SID>`.
- **A policy re-checks every path at clean time.** It refuses the Windows folder (except a few cache folders),
  Program Files, ProgramData, secrets (`.ssh`, Credentials, Protect, Vault), container folders themselves, ancestors of
  your profile, other users' folders and anything on the ignore list.
- **Links are never followed.** A junction or symlink is removed as a link and counts as no space. Cloud-only
  OneDrive files are never downloaded by a scan.
- **Elevation is narrow.** `Cleanup.Elevated.exe` runs with one UAC prompt per action and only talks to the Cleanup
  process that started it, over a named pipe. It only deletes *direct children* of a fixed list of system folders,
  re-checked inside the helper, and its other operations are a fixed allow-list. A cancelled prompt removes nothing.
- **Background work never asks for anything.** Automatic cleaning only takes what Limpar agora would.

## CLI

```
cleanup scan [--include-review]
cleanup clean [--rule ID]… [--include-review] [--yes] [--permanent] [--admin]
cleanup large [--min-size 500MB] [--older-than DAYS]
cleanup lens [PATH]      cleanup disk      cleanup health [--all]
cleanup ignore [list | add PATH | remove PATH]
cleanup history          cleanup undo [ID] [--yes]          cleanup rules
```

Every command is a dry run unless you pass `--yes`. The executable is `cleanup-cli.exe`; the installer adds a
`cleanup.cmd` shim.

## Develop

You need the .NET 10 SDK (pinned in `global.json`). The WinUI app builds with the SDK alone, without Visual Studio.

```powershell
dotnet build Cleanup.sln
dotnet test Cleanup.sln                       # CLEANUP_REAL_RECYCLE=1 also round-trips a file through the real Recycle Bin
src\Cleanup.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Cleanup.exe
```

Set `CLEANUP_INSTANCE=<name>` to run a development copy next to the installed one. With it set, the copy also leaves the
`cleanup:` protocol registration alone.

### Layout

```
src/
  Cleanup.Core/             rules, scan engine, safety, cleaner, history/undo, health, automatic cleaning, links — no Windows APIs
  Cleanup.Platform.Windows/ Recycle Bin, elevation client, health sampler, Wi-Fi, registry, processes
  Cleanup.Elevated/         the UAC helper (allow-listed operations over a named pipe)
  Cleanup.Cli/              cleanup-cli.exe
  Cleanup.App/              WinUI 3 app: themes, tabs, modules, tray
tests/                      xUnit; Core tests run shared/test-vectors from the macOS repo
shared/                     vendored from the macOS repo — do not edit here (scripts/sync-shared.ps1)
```

`shared/` in the macOS repo is the source of truth for the test vectors, the design tokens and the Windows rules. To
change one of them, edit it there, run `swift test`, then run `./scripts/sync-shared.ps1` here. `SharedSourceTests`
fails if a vendored file differs from the hashes in `shared/SOURCE`.

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

- Committers and reviewers: [Lucas Cardoso](https://github.com/LucasBCardoso)
- Approvers: [Lucas Cardoso](https://github.com/LucasBCardoso)

Only binaries built by this repository's GitHub Actions from its own source are signed: `Cleanup.exe`,
`cleanup-cli.exe`, `Cleanup.Elevated.exe`, their `Cleanup*.dll` / `cleanup-cli.dll`, and `Cleanup-Setup-<version>.exe`.
The .NET and Windows App SDK files inside the installer keep Microsoft's own signature. Every signing request is
approved by hand on SignPath. Everyone with access to this repository or the SignPath project uses multi-factor
authentication.

## Privacy

Cleanup has no account, telemetry, analytics or ads, and it never sends your files or anything about them anywhere.
It only connects to the network in these cases:

- **Self-update** (Ajustes › Atualizações, on by default; turning it off keeps only "Procurar agora"): it reads the
  latest release from `api.github.com` and downloads the installer from `github.com`. These are plain GitHub requests
  with the app version in the User-Agent, subject to [GitHub's privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).
- **Atualizador** and the Hoje tile that counts updates: these run Windows' own `winget`, which contacts its
  configured sources (by default Microsoft's). Installing an update downloads it from the publisher of that app.

Everything else (scans, history, settings, logs) stays on the PC in `%LOCALAPPDATA%\Cleanup`.

## License

[MIT](LICENSE). Copyright (c) 2026 Lucas Cardoso.
