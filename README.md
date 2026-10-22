# Gradle Doctor

**Compatibility-aware Gradle and Android Gradle Plugin updates for Android and Flutter projects.**

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![UI](https://img.shields.io/badge/UI-WPF-5C2D91)

Gradle Doctor is a Windows desktop application that scans a folder for Android and Flutter projects, checks their Gradle and Android Gradle Plugin (AGP) versions, and helps you update them safely.

It focuses on compatibility instead of blindly upgrading: changes are planned first, warnings are shown before applying, and an optional build verification can automatically roll back a project if the update breaks it.

---

## Table of Contents

- [Overview](#overview)
- [Features](#features)
- [Requirements](#requirements)
- [Getting Started](#getting-started)
- [Usage](#usage)
- [Safety and Rollback](#safety-and-rollback)
- [Settings](#settings)
- [Compatibility Rules](#compatibility-rules)
- [Keyboard Shortcuts](#keyboard-shortcuts)
- [Configuration and Data](#configuration-and-data)
- [Project Structure](#project-structure)
- [Testing](#testing)
- [Publishing](#publishing)
- [Limitations](#limitations)
- [Contributing](#contributing)
- [License](#license)

---

## Overview

Keeping Gradle and AGP versions aligned across multiple Android and Flutter projects is repetitive and easy to get wrong. A Gradle version that works with one AGP release may be incompatible with another, and some upgrades also require a newer JDK.

Gradle Doctor makes the process visible and reversible:

1. Scan a projects folder.
2. Review the detected Gradle and AGP versions.
3. Choose a target Gradle version.
4. Run a dry run to see exactly what would change.
5. Apply selected updates with backups.
6. Optionally build each project and roll back on failure.

The application never commits to Git and does not modify a project until you confirm the change.

---

## Features

### Project discovery

- Recursively scans a folder for standalone Android and Gradle projects.
- Detects Flutter applications through `pubspec.yaml` and their `android/` project.
- Reads the Gradle wrapper version from `gradle/wrapper/gradle-wrapper.properties`.
- Detects AGP from Gradle scripts and version catalogs, including:
  - `settings.gradle` and `settings.gradle.kts`
  - `build.gradle` and `build.gradle.kts`
  - `gradle/libs.versions.toml`
- Ignores common dependency, build, generated, and platform folders by default.
- Lets you customize the ignored folder names.

### Compatibility-aware planning

- Fetches stable Gradle releases from `services.gradle.org`.
- Shows which versions are already downloaded locally.
- Suggests the newest AGP version compatible with the selected Gradle target.
- Warns about:
  - AGP versions that require a newer Gradle release.
  - Gradle releases that require a newer AGP.
  - Major AGP upgrades.
  - AGP 9.x and its JDK 17+ requirement.
  - Gradle versions newer than the latest AGP-tested release.
- Compatibility rules are stored in an editable JSON matrix.

### Safe updates

- Dry run reports planned changes without writing anything.
- Updates `distributionUrl` in the Gradle wrapper properties.
- Adds or updates `distributionSha256Sum` using distribution-matching checksums (`all` vs `bin`) to prevent wrapper verification errors during builds.
- Updates recognized AGP declarations in build scripts and version catalogs.
- Creates `.gradledoctor.bak` backups next to every modified file.
- Can run a Gradle task after updating and automatically restore backups if the build fails.
- Preserves the original Gradle distribution type (`bin` or `all`).

### Maintenance tools

- Pre-downloads a Gradle distribution into the selected Gradle home without changing the project.
- Lists unused Gradle distributions and version caches with their sizes.
- Protects Gradle versions used by scanned projects from cleanup.
- Supports permanent deletion or sending items to the Recycle Bin with thread-safe execution.
- Stops Gradle daemons for the scanned projects.

### Desktop experience

- Light, Dark, and System themes.
- Real-time bidirectional target synchronization between toolbar picker, project selection, and table cell edits.
- Search and filter discovered projects.
- Details panel for the selected project.
- Live execution log with auto-expansion during updates and copy, save, and clear actions.
- Non-blocking floating progress card with real-time percentage, project counter, live task status, minimization, and drag-and-drop repositioning.
- Keyboard shortcuts for the common workflow.

---

## Requirements

### To run

- Windows 10 or Windows 11.
- .NET 8 Desktop Runtime when using a framework-dependent build.
- A JDK supported by your AGP version. AGP 8.x and 9.x require JDK 17 or newer.
- The Android SDK if you enable build verification.

Gradle Doctor does not bundle Gradle, the Android SDK, or a JDK.

### To build

- Windows 10 or Windows 11.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- A terminal such as PowerShell.

---

## Getting Started

Clone the repository:

```powershell
git clone https://github.com/mostafa-hashem/gradel-doctor.git
cd gradel-doctor
```

Build the application:

```powershell
dotnet build GradleDoctor.csproj
```

Run it:

```powershell
dotnet run --project GradleDoctor.csproj
```

You can also open `GradleDoctor.csproj` directly in Visual Studio 2022.

---

## Usage

### 1. Choose a projects folder

Select the folder that contains your Android and Flutter projects, then click **Scan projects** or press `F5`.

Only projects with a Gradle wrapper are discovered. A Flutter project must contain both `pubspec.yaml` and `android/gradle/wrapper/gradle-wrapper.properties`.

### 2. Review the detected projects

The grid shows each project with:

| Column | Meaning |
| --- | --- |
| Project | Project or Flutter application name |
| Type | Android or Flutter |
| Gradle | Currently configured Gradle version |
| AGP | Detected Android Gradle Plugin version |
| Target | Planned Gradle version |
| Target AGP | Planned AGP version |
| Status | Up to date, will change, warning, or result |
| Warning | Compatibility or validation message |
| Path | Project location on disk |

Use the search box and type filter to narrow the list. Select a row to inspect its details.

### 3. Set a target

Enter a target Gradle version in **Target Gradle**, choose one from the version picker dropdown, or use the default target from Settings.

- Picking or entering a target version automatically updates and recalculates compatibility for all selected projects in real time.
- Selecting any project row in the table or editing its **Target** column syncs the toolbar version picker to match.
- Click **Set target** at any time to re-apply the toolbar target to selected projects.

### 4. Run a dry run

Click **Dry run** or press `Ctrl+Enter`.

This recalculates compatibility, lists planned changes in the log, and reports how much cache space would become unused. Nothing is modified.

### 5. Apply the updates

Click **Apply selected** or press `Ctrl+Shift+Enter`.

The confirmation dialog shows the Gradle and AGP changes, warnings, and any JDK note. After confirmation, Gradle Doctor:

1. Updates the Gradle wrapper properties.
2. Updates recognized AGP declarations.
3. Runs the verification task when enabled.
4. Rolls the project back from `.gradledoctor.bak` files if verification fails.

### 6. Maintain the Gradle home

- **Prepare** downloads the target Gradle distribution into the selected Gradle home.
- **Clean old versions...** lists unused distributions and caches.
- **Stop daemons** stops Gradle daemons used by the scanned projects.

---

## Safety and Rollback

Gradle Doctor is designed to keep every change reviewable and reversible.

- **No silent writes:** a project is only changed after you confirm the apply dialog.
- **Dry run first:** use it to inspect the full plan and compatibility warnings.
- **Backups:** modified wrapper and build files get a sibling file named `<original>.gradledoctor.bak`.
- **Automatic rollback:** when build verification is enabled, a failed build restores the backups.
- **Cleanup protection:** versions currently used by scanned projects are excluded from the cleanup list.
- **Recycle Bin by default:** cleanup items can be sent to the Recycle Bin instead of being permanently deleted.
- **No Git automation:** Gradle Doctor does not stage, commit, or push changes.

Review the resulting diff in your version control system before committing.

---

## Settings

Open **Settings & environment** in the main window.

| Setting | Description |
| --- | --- |
| **Gradle home** | Custom Gradle user home. Leave empty to auto-detect `GRADLE_USER_HOME` or `%USERPROFILE%\.gradle`. |
| **Skip folders** | Folder names ignored during project scanning. Add names such as `.git` or `node_modules`, not full paths. |
| **Default target** | Default Gradle version used for new scans and by **Use as target for selected**. |
| **Auto-update AGP** | Suggests the newest AGP version compatible with the target Gradle version. |
| **Build after update and roll back on failure** | Runs the configured task after an update and restores backups if it fails. |
| **Task** | Gradle task used for verification. Default: `assembleDebug`. |
| **Timeout** | Maximum verification time in minutes. Default: `15`. |
| **Theme** | Light, Dark, or System. |
| **Edit compatibility rules** | Opens the editable `compat.json` compatibility matrix. |

Settings are saved automatically and restored on the next launch.

---

## Compatibility Rules

The compatibility matrix lives in [`compat.json`](compat.json). On first use, Gradle Doctor creates a per-user copy at:

```text
%APPDATA%\GradleDoctor\compat.json
```

The file contains these mappings:

| Key | Purpose |
| --- | --- |
| `agpMinGradle` | Minimum Gradle version for each AGP minor version. |
| `agpDefaultGradle` | Recommended Gradle version for each AGP minor version. |
| `gradleMinAgp` | Minimum AGP version required by a Gradle release. |
| `agpMinJdk` | Minimum JDK major version for an AGP release. |
| `note` | Free-form note shown in the file. |

You can edit the file directly or use **Edit compatibility rules** in Settings. Changes apply to the next dry run or apply operation.

Compatibility warnings are advisory. They help you make a decision but do not prevent you from applying a change.

---

## Keyboard Shortcuts

| Shortcut | Action |
| --- | --- |
| `F5` | Scan projects |
| `Ctrl+F` | Focus the project search box |
| `Ctrl+Enter` | Run a dry run |
| `Ctrl+Shift+Enter` | Apply selected updates |
| `Esc` | Cancel the current long-running operation |

---

## Configuration and Data

Gradle Doctor stores per-user data only. It contains no telemetry and does not send project data to a server.

| Path | Purpose |
| --- | --- |
| `%APPDATA%\GradleDoctor\settings.json` | User settings, skip folders, default target, theme, and flags. |
| `%APPDATA%\GradleDoctor\compat.json` | Per-user compatibility matrix. |
| `%APPDATA%\GradleDoctor\gradle-versions.json` | Cached list of stable Gradle releases. |
| `%TEMP%\GradleDoctor-crash.log` | Crash details, written only if an unhandled error occurs. |

Network access is used for:

- Fetching the stable Gradle release list.
- Fetching distribution checksums.
- Pre-downloading a Gradle distribution through the project wrapper.

If the version service is unavailable, the application falls back to a cached or built-in list.

---

## Project Structure

| Path | Contents |
| --- | --- |
| `MainWindow.xaml`, `MainWindow.xaml.cs` | Main UI and workflow orchestration. |
| `Core.cs` | Settings, project scanner, wrapper/AGP updates, cleanup, and Gradle runner. |
| `Services/` | Gradle release service, wrapper downloader, AGP resolver, Java detection, and themes. |
| `Dialogs/` | Apply and cleanup dialogs plus shared dialog helpers. |
| `Themes/` | Shared WPF resources and Light/Dark theme dictionaries. |
| `Controls/` | Toast notification control. |
| `compat.json` | Default AGP/Gradle compatibility matrix. |
| `GradleDoctor.Tests/` | xUnit test project. |
| `Assets/` | Application icon. |

---

## Testing

Run the test suite with:

```powershell
dotnet test GradleDoctor.Tests\GradleDoctor.Tests.csproj
```

The tests cover version comparison, compatibility rules, project scanning, cleanup behavior, and default settings.

---

## Publishing

Create a framework-dependent release build:

```powershell
dotnet publish GradleDoctor.csproj -c Release -o publish
```

The target machine needs the .NET 8 Desktop Runtime.

For a self-contained Windows x64 build:

```powershell
dotnet publish GradleDoctor.csproj -c Release -r win-x64 --self-contained true -o publish
```

Keep `compat.json` next to the published executable so the default compatibility matrix is available.

---

## Limitations

- Windows-only, because the UI is built with WPF.
- Only projects with a Gradle wrapper are discovered.
- AGP detection covers standard plugin, classpath, alias, and version-catalog declarations. Highly customized build logic may require manual changes.
- Build verification requires a working JDK and, for Android projects, the Android SDK.
- Compatibility rules are guidance and may not cover every plugin or custom build setup.
- The latest Gradle list and checksums require network access; offline mode uses cached data and may skip checksum verification.

---

## Contributing

Contributions are welcome.

1. Fork the repository.
2. Create a focused branch.
3. Make your change and keep the existing UI and code style.
4. Build the project and run the tests.
5. Open a pull request with a clear description of the problem and the solution.

When reporting a bug, include:

- Windows version.
- .NET version.
- Gradle and AGP versions involved.
- Steps to reproduce.
- The saved execution log, if available.

---

## License

No license file is currently included. Add a license before distributing the project or accepting external contributions.
