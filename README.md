# OpenAppMover

Move an installed Windows application to another PC **without its original installer**: OpenAppMover finds everything that
belongs to the application (files, registry, services, scheduled tasks, shortcuts, file associations, user data…),
packs it into a `.appmove` archive, and deploys it on another machine — with a full preview, automatic rollback on
failure and a clean uninstallation.

> **Status: validated on Windows 10 (22H2).** Applications of every supported installer type (MSI, WiX Burn,
> Inno Setup, NSIS, InstallShield, Squirrel) were exported and imported on real machines; the automated tests run on
> a simulated Windows (fake registry and file system).
> Drivers are not transferred (see *Known limitations*). Keep a backup or a restore point before deploying.

> **Licenses.** Moving software to another computer may be forbidden by its license agreement (EULA), and some
> applications are bound to the machine they were activated on. You are responsible for respecting the licenses of the
> applications you transfer.

## Requirements

- Windows x64, on both machines, with administrator rights (the executables request elevation).
- Two builds of the same application:

| Windows | Executables | Needs |
|---|---|---|
| 10, 11 | `openappmover.exe` (command line), `openappmover-gui.exe` (graphical interface) | nothing: self-contained .NET 8 |
| 7 SP1, 8.1 (and later) | `openappmover-legacy.exe`, `openappmover-gui-legacy.exe`, each with its `.exe.config` | .NET Framework 4.8 |

Windows 8 (8.0) is not supported: .NET Framework 4.8 cannot be installed there (free upgrade to Windows 8.1).

On Windows 7, keep the `.exe.config` file next to its executable: without .NET Framework 4.8, Windows then offers to
install it. Recommended updates there: KB4474419 (SHA-2 code signing; the executables are also signed with SHA-1) and,
for applications built with Visual C++ 2015 or later, KB2999226 (Universal C Runtime).

If the .NET Framework 4.8 installer (use the offline one, `ndp48-x86-x64-allos-enu.exe`) fails on Windows 7 SP1:
- *"A certificate chain processed, but terminated in a root certificate which is not trusted"*: import the Microsoft
  Root Certificate Authority 2011 (`MicRooCerAut2011_2011_03_22.crt`, from `http://www.microsoft.com/pki/certs/`) into
  the machine's trusted roots: `certutil -addstore root MicRooCerAut2011_2011_03_22.crt` (elevated prompt).
- *"The timestamp signature and/or certificate could not be verified or is malformed"*: install KB4490628 (servicing
  stack), then KB4474419 (SHA-2), reboot; if it persists, also import `MicRooCerAut_2010-06-23.crt` the same way and
  check the system date.

Windows 7 limitations:
- **Record usage** (dynamic analysis) is not available: it needs Windows 8 or later.
- Scheduled tasks exported from Windows 8 and later are registered with the Windows 7 schema (settings it does not know
  are removed).
- Before deploying, the preview reports the applications that cannot run there: executables made for Windows 8 and
  later, .NET 7 and later, missing Universal C Runtime.

## Graphical interface

`openappmover-gui.exe` has three tabs:

- **Export** — pick an application, review what was found, grouped by confidence (Certain / Probable / Possible, each
  with the reason and warnings), optionally **Record usage…** (see *Dynamic analysis*), then save the package
  (optionally encrypted with a password).
- **Import** — open a package (its integrity is checked), choose the install directory and which user of this PC
  receives the data of each source user, look at the **preview** of every operation and of the points of attention,
  then deploy.
- **Deployments** — applications installed with OpenAppMover on this PC, and their uninstallation.

## Command line

```
openappmover list [--all-users] [--hidden] [--json]
openappmover inspect <app> [--json]
openappmover collect <app> [-o selection.json] [--trace trace.json] [--json]
openappmover trace <app> [--launch] [-o trace.json]
openappmover export <app> [-o package.appmove] [--items selection.json] [--include-possible]
                      [--encrypt] [--no-vss] [--trace trace.json] [--force]
openappmover verify <package>
openappmover deploy <package> [--install-dir <dir>] [--user-map 0=<user|SID>,…] [--dry-run] [--force] [--yes]
openappmover deployments
openappmover uninstall <deployment id|app> [--yes]
```

`<app>` is an id, an Uninstall key name or part of the application name. `openappmover help` describes every option.

Typical transfer:

```
# Source PC
openappmover collect "Notepad++" -o npp.json      # review; edit "Selected" in npp.json if needed
openappmover export "Notepad++" --items npp.json -o npp.appmove

# Target PC
openappmover deploy npp.appmove --dry-run         # shows everything that would be done
openappmover deploy npp.appmove
openappmover uninstall "Notepad++"                # or from "Apps & features"
```

Environment variables: `OPENAPPMOVER_PASSWORD` (password of encrypted packages, for automation — passwords are never
accepted on the command line), `OPENAPPMOVER_7ZIP` (another 7-Zip built with the NSIS decompiler, see below).

Logs: `%LOCALAPPDATA%\OpenAppMover\logs`.

## How it works

### 1. Inventory and installer detection

Applications are read from the Uninstall keys (machine 64/32-bit, every user profile — hives of users who are not
logged on are mounted). The installer technology is detected with a score and its evidence: MSI, WiX Burn,
Inno Setup, NSIS, InstallShield, Squirrel, ClickOnce (not transferable).

### 2. Collection

| Source | What it finds |
|---|---|
| Uninstall entry, install folders | install directories (also derived from MSI components), the Uninstall key |
| Windows Installer | installed components (registry), the `Registry` table of the cached `.msi`; for a WiX Burn bundle, those of every MSI package it chains |
| Inno Setup | the uninstall log `unins000.dat` |
| NSIS | the script of the uninstaller, decompiled by the embedded 7-Zip |
| InstallShield, Wise, custom installers | paths listed in their logs (`setup.ilg`, `install.log`…) |
| System | shortcuts, services and drivers, scheduled tasks, Run keys, App Paths, environment variables (PATH entries only), firewall rules, event log sources, COM classes and Explorer shell extensions (context menus…), ProgIDs, URL protocols and file associations |
| Heuristic | data and settings named after the application or its publisher (AppData, ProgramData, Documents, `Software\Vendor\Product`, UAC VirtualStore) |
| Dynamic analysis (optional) | what the application actually reads and writes while you use it |

Every item has a confidence: **Certain** and **Probable** items are selected by default, **Possible** ones (guesses,
shared or system files) are not. Prerequisites (Visual C++, .NET, WebView2, DirectX) are detected from the
executables and checked on the target.

### 3. Package (`.appmove`)

A ZIP archive with the files, `registry.json` and `manifest.json` (SHA-256 of every file, users, shortcuts, tasks,
services, prerequisites, issues). Paths are **tokenized** — `{InstallDir}`, `{ProgramFiles}`, `{U0.AppData}`… — in file
names, registry values, task definitions and configuration files (`C:\…`, JSON-escaped `C:\\…` and `C:/…` forms), so the
application can be deployed in another folder, for another user, or on a PC where Program Files is elsewhere.

- Files locked by the running application are read from a **Volume Shadow Copy** (`--no-vss` to skip them).
- `--encrypt`: AES-256 + HMAC-SHA256 of the whole archive, key derived from the password (PBKDF2, 600,000 iterations).

### 4. Deployment

The plan is computed first (`--dry-run` shows it): folders, files, shortcuts (recreated when their target moved),
services (through the Service Control Manager), registry, PATH entries, scheduled tasks (accounts rewritten for the
target user), and an Uninstall entry pointing to OpenAppMover. Checks: application already installed, disk space,
missing prerequisites, driver and firewall warnings.

- **Newer files are kept**: an executable or DLL already on the target with a higher file version is not replaced.
- Every change is **journaled before it is made** (with a backup of replaced files and previous registry values) in
  `%ProgramData%\OpenAppMover\Deployments\<id>`: a failure rolls everything back, and the uninstallation replays the journal
  backwards. OpenAppMover copies itself to `%ProgramFiles%\OpenAppMover` so that the Uninstall entry keeps working.

### Dynamic analysis

`openappmover trace <app> [--launch]` (or **Record usage…** in the interface) follows the application's processes with a
kernel ETW session while you use it, then `collect`/`export --trace` adds the data folders and settings keys it used.
It needs Windows 8 or later.

## Known limitations

- Firewall rules are written to the registry and only apply after the firewall service restarts.
- Prerequisites are detected but not installed; drivers are not deployed; service account passwords must be set again.
- A target file in use makes the deployment fail (and roll back).
- Child MSI packages of WiX Burn bundles are not followed; Inno Setup `.ini` changes are not collected; NSIS paths
  computed in variables are ignored.
- Microsoft Store (MSIX) applications are not supported.

## Building

```
./build.sh
```

Builds, runs the tests and publishes into `dist/` the four executables (see *Requirements*), the license and the
third-party notices. It runs on Linux or Windows; the .NET 8 SDK and the NuGet packages are installed in `.build/` when
missing. The .NET Framework 4.8 tests run on Windows, and elsewhere with Mono when it is installed (xUnit console
runner fetched into `.build/`); without Mono they are skipped (the CI runs them).

Every library targets `net8.0` and `net48`. The .NET 8 APIs missing from .NET Framework are in
`src/OpenAppMover.Core/Compat`: `Portable` (static methods, both targets) and extension methods with the .NET 8
signatures (.NET Framework only). The legacy executables (Windows 7 SP1 and 8.1) merge their dependencies with Costura.Fody.

`third_party/7-Zip/7zz.exe` is produced by `tools/build-7zip.sh` (see below); the executables embed it. It is built with
the MinGW-w64 GCC of Debian (extracted into `.build/`, no root needed) and links with `msvcrt.dll`, so that it runs on
Windows 7.

The application icon `src/OpenAppMover.Gui/Assets/openappmover.ico` is generated by `tools/build-icon.sh` from
`openappmover.svg` (32 px and larger) and `openappmover-small.svg` (16 to 24 px, drawn on a 16-pixel grid); run it after
changing either drawing. It needs Node.js.

### Releases

The GitHub Actions workflow (`.github/workflows/build.yml`) builds and tests every push and pull request, and uploads
the executables as a workflow artifact. Pushing a tag `v<version>` also publishes the GitHub Release:

```
git tag v1.1.0
git push origin v1.1.0
```

The tag must match `<Version>` in `Directory.Build.props`, and `CHANGELOG.md` must have a section for that version: it
becomes the release notes. The release holds the four executables (with the `.exe.config` of the legacy ones) and a zip with them, the license and the
third-party notices, attached a few minutes after the tag is pushed (when the workflow ends; a release drafted on the
GitHub page with a new tag gets them too). A version with a suffix (`1.1.0-beta.1`) is published as a pre-release. Releases are not signed.

### Signing

The executables and the embedded 7-Zip are signed (Authenticode, timestamped) when `SIGN_PFX` is set: a SHA-1 signature
and a nested SHA-256 one, since Windows 7 without KB4474419 only checks the first (`SIGN_SHA1=0`: SHA-256 only).

```
tools/make-test-cert.sh   # once: self-signed test certificate in .build/signing
SIGN_PFX=.build/signing/openappmover-test.pfx SIGN_PASSWORD_FILE=.build/signing/password.txt ./build.sh
```

7-Zip is signed as a copy in `.build/signed/7zz.exe`, embedded instead of `third_party/7-Zip/7zz.exe` (MSBuild property
`SevenZipExe`): the committed binary stays the unsigned reproducible build whose SHA-256 is listed in its `README.txt`.

`tools/sign.sh` uses `osslsigncode` (fetched from the Debian archive into `.build/` when it is not installed);
`SIGN_TIMESTAMP_URL` changes the timestamp server, and an empty value skips the timestamp. On Windows, `signtool`
does the same:

```
signtool sign /fd SHA1 /f openappmover.pfx /p <password> /tr http://timestamp.digicert.com /td SHA1 dist\*.exe
signtool sign /as /fd SHA256 /f openappmover.pfx /p <password> /tr http://timestamp.digicert.com /td SHA256 dist\*.exe
```

A self-signed certificate is trusted only on the PCs where it is installed. On each test PC, in an administrator
PowerShell:

```
Import-Certificate -FilePath openappmover-test.cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath openappmover-test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
```

The UAC prompt then shows "Keep The Faith (test)" as the verified publisher. Remove the certificate from both stores
(`certlm.msc`) after the tests. For a public release, sign with a certificate from a certification authority.

### Projects

| Project | Role |
|---|---|
| `OpenAppMover.Core` | models, registry and file system abstractions, path tokens, parsers (shell links, PE, Inno log, NSIS script, MSI) |
| `OpenAppMover.Inventory` | installed applications |
| `OpenAppMover.Detection` | installer detection |
| `OpenAppMover.Collectors` | collection session and collectors |
| `OpenAppMover.Packaging` | package writer, manifest, encryption |
| `OpenAppMover.Deployment` | package reader, deployment planner and executor, Windows services, tasks and shortcuts |
| `OpenAppMover.Host` | Windows composition shared by both interfaces (VSS, ETW, embedded 7-Zip) |
| `OpenAppMover.Cli`, `OpenAppMover.Gui` | command line and WPF interface |
| `tests/OpenAppMover.Tests` | xUnit tests on a simulated Windows |

## License

OpenAppMover is released under the [MIT License](LICENSE) — Copyright (c) 2026 Keep The Faith.

## Third-party software

- **7-Zip** 26.05 by Igor Pavlov (GNU LGPL), built from the official source with the NSIS script decompiler enabled
  (`NSIS_SCRIPT`, disabled in official builds) and without RAR code — see `third_party/7-Zip/README.txt` and
  `tools/build-7zip.sh`, which reproduce the binary.
- NuGet packages: CommunityToolkit.Mvvm (MIT), Microsoft.Diagnostics.Tracing.TraceEvent (MIT), Microsoft.Extensions.Logging
  (MIT), Serilog (Apache-2.0), System.Management (MIT), xUnit (Apache-2.0).
