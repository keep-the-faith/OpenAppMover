# Changelog

All notable changes to OpenAppMover are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.1.1] - 2026-11-23

### Added
- Prerequisites: ActiveX controls of other products hosted in the dialogs of the executables (e.g. Adobe Flash Player,
  removed from Windows 10 and 11), checked before deploying, with what to do for Flash Player.
- Collection: `.ini` files that legacy applications keep in the Windows folder (named without a folder in their
  executables), and their UAC VirtualStore copy.

### Fixed
- Components installed in a `Vendor\Product` folder of System32 or SysWOW64 (Adobe Flash Player ActiveX:
  `SysWOW64\Macromed\Flash`, and its 64-bit part in System32) had no install folder: only their Uninstall key was
  exported, without their files and COM classes.

## [1.1.0] - 2026-11-16

### Added
- Windows 7 SP1 and 8.1 support (not 8.0, where .NET Framework 4.8 cannot be installed): `openappmover-legacy.exe` and `openappmover-gui-legacy.exe` (.NET Framework 4.8, with
  their `.exe.config`), next to the .NET 8 executables for Windows 10 and 11.
- Prerequisites: minimum Windows version of the application (executables made for Windows 8 and later, .NET 7 and
  later) and Universal C Runtime, checked before deploying.
- Scheduled tasks exported from Windows 8 and later are adapted to the Windows 7 schema when deployed there.

### Changed
- The embedded 7-Zip is built with the MinGW-w64 GCC of Debian and links with `msvcrt.dll` (no Universal C Runtime),
  so that it runs on Windows 7.
- Executables signed with SHA-1 and SHA-256 (dual signature), for Windows 7 without KB4474419.

### Fixed
- Explorer shell extensions (context menu, drag and drop, icon overlay, preview and thumbnail handlers) are collected
  with their COM class: the context menu of applications such as 7-Zip was not restored.

### Known limitations
- Windows 7: no usage recording (dynamic analysis), which needs Windows 8 or later.

## [1.0.0] - 2026-11-14

First version. Validated on Windows 10 (22H2): applications of every supported installer type were exported and
imported on real machines; the automated tests run on a simulated Windows.

### Added

#### Inventory and detection
- List of the installed applications from the Uninstall keys: machine (64-bit and 32-bit views) and every user profile;
  hives of users who are not logged on are mounted (`NTUSER.DAT`, and `UsrClass.dat` for their classes).
- Entries hidden by "Apps & features" (updates, system components…) filtered out, shown with `--hidden`.
- Installer detection with a confidence score and its evidence: MSI, WiX Burn, Inno Setup, NSIS, InstallShield,
  Squirrel, ClickOnce (reported as not transferable).

#### Collection
- Install directories from the Uninstall entry, the installer data, the icon and uninstaller folders, and the MSI
  components, with the case of the folder on the disk; too broad folders (drive root, Program Files, Windows, user
  profile…) are rejected.
- Windows Installer: installed components read from the registry and the `Registry` table of the cached `.msi`;
  registry key paths (including value names containing a backslash), GAC assemblies, folder key paths and the `C:\?`
  notation handled; files and folders missing on the disk skipped.
- WiX Burn bundles (e.g. the Python installer): the MSI packages chained by the bundle are found through Burn's
  dependency registration and collected like MSI applications; the cached setup is kept for uninstallation.
- Inno Setup: uninstall log (`unins000.dat`) reader.
- NSIS: uninstaller script decompiled by an embedded 7-Zip; both shell contexts are tried and only existing paths and
  keys are kept.
- InstallShield, Wise and custom installers: paths listed in their logs (`setup.ilg`, `install.log`…); a listed folder
  is kept only when it, or one of its parents, is named after the application or its publisher.
- System integrations: shortcuts (Start menu, desktop, taskbar, advertised MSI shortcuts), services and drivers,
  scheduled tasks (programs and COM handlers), Run keys, App Paths, environment variables (PATH entries only), firewall
  rules, event log sources, COM classes and type libraries, ProgIDs, URL protocols and file associations.
- Heuristic search of data and settings named after the application or its publisher (AppData, LocalAppData,
  LocalLow, ProgramData, Documents, `Software\Vendor\Product`, UAC VirtualStore); temporary folders never collected.
- Dynamic analysis: kernel ETW trace of the application's processes while it is used (`trace` command,
  **Record usage…** button with a non-blocking banner), adding the data folders and settings keys it actually wrote
  or read.
- Confidence levels (Certain, Probable, Possible) with reasons and warnings; duplicates and nested items merged;
  editable selection file (`collect -o`, `export --items`).
- Prerequisites detected from the executables (imports, CLR header, `runtimeconfig.json`): Visual C++ 2005-2022,
  .NET Framework, .NET, DirectX, WebView2.

#### Package
- `.appmove` archive (ZIP64) with the files, `registry.json` and `manifest.json` (SHA-256, size, date and attributes of
  every file, users, folders, shortcuts, tasks, services, prerequisites, issues).
- Tokenized paths (`{InstallDir}`, `{ProgramFiles}`, `{U0.AppData}`…) in file names, registry values, task
  definitions and configuration files (including JSON-escaped and forward-slash forms), adapted on deployment.
- Files locked by the running application read from a Volume Shadow Copy (`--no-vss` to skip them).
- Optional password encryption (`--encrypt`): AES-256-CBC + HMAC-SHA256, PBKDF2-SHA256 (600,000 iterations).
- `verify` command (SHA-256 of every file).

#### Deployment
- Plan computed before any change (`--dry-run`, preview in the interface), with checks: application already
  installed, disk space, missing prerequisites, existing services and tasks, drivers, firewall rules, service accounts.
- Install directory relocated or chosen (`--install-dir`); source users mapped to target users (`--user-map`).
- Files, registry (32/64-bit views, target user hives), shortcuts, services, scheduled tasks, PATH entries and UAC
  VirtualStore restored; newer executables and DLLs already on the target are kept; services that start with Windows
  are started.
- Every change journaled before it is made, with backups; automatic rollback on failure.
- Uninstall entry pointing to OpenAppMover; `uninstall` command (also from "Apps & features") replays the journal
  backwards; `deployments` command.

#### Interfaces
- Command line `openappmover.exe`: `list`, `inspect`, `collect`, `trace`, `export`, `verify`, `deploy`, `deployments`,
  `uninstall`; JSON output; passwords typed without echo or read from `OPENAPPMOVER_PASSWORD`.
- Graphical interface `openappmover-gui.exe` (WPF): Export, Import and Deployments tabs, review grouped by confidence,
  usage recording, encryption, preview, progress, deployment warnings.
- Application icon; SVG sources and `tools/build-icon.sh` to regenerate the `.ico`.
- Both interfaces in English; logs in `%LOCALAPPDATA%\OpenAppMover\logs`.
- Native tools (7-Zip, `KernelTraceControl.dll`) embedded in the single-file executables, extracted to
  `%ProgramFiles%\OpenAppMover\tools` and checked (SHA-256) before use.

#### Project and build
- MIT License (Copyright (c) 2026 Keep The Faith), also in the executables' properties and in `dist/`.
- `build.sh`: build, tests and self-contained single-file publication for Windows x64, from Linux or Windows.
- `tools/build-7zip.sh`: 7-Zip 26.05 built from the official source with the NSIS script decompiler enabled and without
  RAR code (LGPL); its notices shipped in `dist/third-party/7-Zip`.
- GitHub Actions workflow building, testing and publishing on Windows; a `v<version>` tag publishes the GitHub
  Release (executables, zip, notes from this changelog).
- Optional Authenticode signature of the executables and of the embedded 7-Zip (`SIGN_PFX`, `tools/sign.sh`);
  `tools/make-test-cert.sh` creates a self-signed test certificate.

### Known limitations
See the *Known limitations* section of the [README](README.md).
