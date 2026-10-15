# Installer and releases

## What the MSI does

`EasyPDF.wxs` builds a **per-user** MSI. It installs into `%LOCALAPPDATA%\Programs\EasyPDF` and
writes only `HKCU`, so no administrator rights are needed to install, upgrade or uninstall. It adds
a Start menu shortcut under `DYNATEC`, and registers EasyPDF as a program that opens PDF files: in
**Open with**, and in **Settings > Apps > Default apps**. It does not make itself the default PDF
viewer - Windows leaves that choice to the user, and so does EasyPDF.

There is no wizard: the package has no UI authoring, so double-clicking it shows a progress
window, installs, and starts the app. A silent install (`/qn`) never starts it.

## User data is never touched

Settings (where each document was left, the recent and pinned files) and the log live in
**`%LOCALAPPDATA%\EasyPDF`**, a different folder from the installation. The installer has no
component, directory or custom action pointing there, so install, upgrade, repair and uninstall
all leave it exactly as it is. Keep it that way: anything authored under that path would let MSI
delete it on uninstall. `build\publish.ps1` refuses an `-OutDir` inside that folder for the same
reason.

## Build a release locally

One-time setup: install the .NET 10 SDK, then

```powershell
dotnet tool install --global wix --version 5.0.2
```

WiX is pinned to 5: from 6 onwards it asks for the paid Open Source Maintenance Fee licence. Then,
from the repository root:

```powershell
.\build\release.ps1 -Version 1.2.3
```

That publishes the app self-contained (it needs no .NET on the machine), packages the MSI, zips a
portable copy and writes checksums into `artifacts\`: `EasyPDF-1.2.3-x64.msi` (the installer),
`EasyPDF-1.2.3-win-x64.zip` (unzip anywhere and run `EasyPDF.exe`) and `SHA256SUMS.txt`. Add
`-SkipPublish` to repackage without rebuilding.

Versions are three or four numbers. A `-prerelease` suffix is rejected: MSI compares numbers
only, so `1.2.3-beta` would not upgrade `1.2.3`.

## Cut a release

```powershell
git tag v1.2.3
git push origin v1.2.3
```

`.github/workflows/release.yml` runs on any `v*` tag - or manually via **Run workflow** with a
version - executes `build\release.ps1` and publishes a GitHub Release with the three files attached
and notes generated from the commits.

## How EasyPDF updates itself

`src\EasyPDF\Services\Updater.cs` asks `https://api.github.com/repos/bendikme/EasyPDF/releases/latest`
once a day. When the release's tag is a newer version than the running build (its
`InformationalVersion`, which `build\publish.ps1` stamps from `-Version`), it downloads the asset
named exactly `EasyPDF-<version>-x64.msi` into `%LOCALAPPDATA%\EasyPDF\updates`, and keeps it only if
its size and SHA-256 match the release and its `SHA256SUMS.txt`. So every release must attach both,
which `build\release.ps1` and the workflow do.

The downloaded package installs when EasyPDF closes, as `msiexec /i <msi> /qn /norestart` - silent,
so it starts nothing - or at once from the header's **Update** button, as `msiexec /i <msi>`, whose
progress window ends by starting the new EasyPDF (the `LaunchApplication` action); the documents that
were open are noted beforehand and opened again. Only a copy running from the MSI's folder installs
updates; a portable or development copy is pointed to the release page instead. A debug build checks
only when asked; `EASYPDF_UPDATES=off` switches checking off, `=soon` has the first check after two
seconds instead of thirty.

## Notes

- The `UpgradeCode` GUID in `EasyPDF.wxs` must never change: it is what makes a new build replace
  the old one instead of installing beside it.
- Silent install: `msiexec /i EasyPDF-1.2.3-x64.msi /qn /norestart`; uninstall:
  `msiexec /x EasyPDF-1.2.3-x64.msi /qn`. `LAUNCHAPP=0` stops a visible install from starting the app.
- After installing, the package runs `EasyPDF.exe --associations-changed`, which only tells
  Explorer that file associations changed (so "Open with" shows EasyPDF at once) and exits.
