# Postzilla

Postzilla is a lightweight Windows API client for **HTTP** and **GraphQL**, built with WinUI 3. Organise requests into collections, switch between environments, import from cURL or Postman, and explore GraphQL schemas with autocomplete and docs.

Postzilla was previously called Brunozilla. On first launch it moves your existing collections and environments from `%LOCALAPPDATA%\Brunozilla` to `%LOCALAPPDATA%\Postzilla`. If you had Brunozilla installed, uninstall it from Windows Settings > Apps.

## Install

Requires Windows 10 1809 or later. Installs per user, no admin rights needed.

### Option 1: PowerShell one-liner

```powershell
& ([scriptblock]::Create((irm "https://raw.githubusercontent.com/judgewest2000/postzilla/main/install.ps1")))
```

This downloads the latest release for your architecture (x64, ARM64 or x86) to `%LOCALAPPDATA%\Programs\Postzilla`, then adds a Start Menu shortcut and an Apps & Features entry.

Options (append after the closing parentheses):

- `-Version v1.0.0` installs a specific release
- `-InstallDir <path>` installs somewhere else
- `-Uninstall` removes Postzilla

### Option 2: Setup.exe

Download `Postzilla-Setup-<arch>.exe` from the [latest release](https://github.com/judgewest2000/postzilla/releases/latest) and run it.

### Uninstall

Use Windows Settings > Apps, or run the one-liner above with `-Uninstall`.

## Features

- HTTP requests with params, headers, body and auth, plus `{{variable}}` substitution from collections and environments
- GraphQL editor (Monaco) with schema autocomplete (Ctrl+Space), docs panel (F1) and formatting (Shift+Alt+F)
- Import from cURL and Postman collections
- Send with Ctrl+Enter or F5

## Updates

On startup, Postzilla checks GitHub for a newer release and asks whether to update. Updating downloads the matching `Postzilla-Setup-<arch>.exe`, verifies its SHA-256 checksum, upgrades in place and restarts the app. You can also use **Check for updates** in the About dialog.

## Build from source

Requires the .NET 9 SDK.

```powershell
# Runs tests, then publishes a self-contained build to .\publish\win-x64
.\publish.ps1 -Zip
```

## Publish a release

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`) and the GitHub CLI.

```powershell
.\installer\build-installer.ps1 -Platform x64 -Version 1.0.1
gh release create v1.0.1 publish\Postzilla-win-x64.zip publish\Postzilla-Setup-x64.exe --title v1.0.1
```

`-Version` is stamped into the app, so the release tag must match it for the update check to work. The zip must be named `Postzilla-win-<arch>.zip` for `install.ps1` to find it. Repeat for `ARM64` and `x86` as needed.

## License

[MIT](LICENSE). Copyright (c) 2026 James West.
