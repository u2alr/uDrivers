# uDrivers

A fast, modern Windows driver updater that installs **official, signed drivers from Windows Update** — no third-party driver sites, no bundleware.

## Features

- **Finds driver updates** for your hardware from Microsoft's Windows Update catalog (the same drivers Windows lists under *Optional updates*).
- **Shows what changes** — installed driver vs. the new one, per device.
- **Installs selected updates** with per-device progress and clear results.
- **Restore point first** (on by default) so you can roll back with System Restore.
- **Installed drivers view** — searchable list of every driver with version, provider and date.
- **Auto-updates itself** from this repo's GitHub Releases.
- Native WinUI 3 (Fluent design, Mica, light/dark), self-contained — nothing else to install.

## Install

Download `uDrivers-win-Setup.exe` from the [latest release](https://github.com/u2alr/uDrivers/releases/latest) and run it. uDrivers asks for administrator rights on launch because Windows requires them to install drivers; if you decline, it runs in read-only mode.

Requires Windows 10 1809 or later, x64.

## Build from source

- Visual Studio 2026 (or 2022 17.14+) with the **WinUI application development** workload, or the .NET 10 SDK.

```powershell
dotnet build src/uDrivers/uDrivers.csproj -c Release -r win-x64 -p:Platform=x64
```

Or open `src/uDrivers/uDrivers.csproj` in Visual Studio and run (start VS as administrator to test installs).

## Releasing a new version

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The **Release** workflow builds the app, packs it with [Velopack](https://velopack.io), and publishes a GitHub Release containing the installer and update packages. Installed copies pick it up on their next update check.

## How it works

| Part | File |
| --- | --- |
| Installed driver inventory (WMI `Win32_PnPSignedDriver`) | `Services/DriverScanner.cs` |
| Search / download / install via Windows Update Agent API | `Services/WindowsUpdateDriverService.cs` |
| System Restore point | `Services/RestorePointService.cs` |
| Self-update from GitHub Releases | `Services/AppUpdateService.cs` |
| UI | `MainWindow.xaml`, `ViewModels/MainViewModel.cs` |

Logs are written to `%LocalAppData%\uDrivers\logs`.

## Roadmap

- Manufacturer catalogs (Intel, AMD, NVIDIA, Dell, HP, Lenovo) for drivers newer than Windows Update carries
- Driver backup and one-click rollback per device
- Scheduled background checks with notifications
- ARM64 build
