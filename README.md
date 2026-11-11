<div align="center">

  <img src="Reepax/Assets/app.png" alt="Reepax" width="88" height="88" />

  ---

  <p>
    <a href="https://github.com/Biiitz/Reepax/actions/workflows/release.yml"><img src="https://img.shields.io/github/actions/workflow/status/Biiitz/Reepax/release.yml?style=flat-square&amp;label=Build%20%26%20release&amp;labelColor=1a1a1a&amp;color=303030" alt="Build and release status" /></a>
    <a href="Reepax.Tests"><img src="https://img.shields.io/badge/Tests-xUnit-303030?style=flat-square&amp;labelColor=1a1a1a" alt="Tests: xUnit" /></a>
    <a href="https://github.com/Biiitz/Reepax/releases"><img src="https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20x64-303030?style=flat-square&amp;labelColor=1a1a1a" alt="Platform: Windows 10 and 11, x64" /></a>
    <a href="Reepax/Reepax.csproj"><img src="https://img.shields.io/badge/.NET-8.0-303030?style=flat-square&amp;labelColor=1a1a1a" alt=".NET 8.0" /></a>
    <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-303030?style=flat-square&amp;labelColor=1a1a1a" alt="License: MIT" /></a>
  </p>

  <p>
    <a href="https://github.com/Biiitz/Reepax/releases/latest/download/setup.exe"><strong>Download for Windows</strong></a>
    &nbsp;&middot;&nbsp; <a href="https://github.com/Biiitz/Reepax/releases">Release notes</a>
    &nbsp;&middot;&nbsp; <a href="https://github.com/Biiitz/Reepax/issues">Report an issue</a>
    &nbsp;&middot;&nbsp; <a href="CONTRIBUTING.md">Contribute</a>
  </p>

</div>

<h2 align="center">Overview</h2>

Reepax brings download pages, related files, and archive processing into one workspace. Open a link in the integrated browser, interact with the website, and let Reepax handle the captured download. Files stay organized in packages, with an overview of download activity and the tasks that follow.

The project is in active development. Features and behavior may change between releases.

See the [Reepax website](https://reepax.pages.dev/#preview) for screenshots of the app and its interface.

<h2 align="center">Features</h2>

| Area | What it offers |
| :--- | :--- |
| **Integrated browser** | Browse download pages inside the app with content and popup blocking. |
| **Package workspace** | Group related files, manage download destinations, and choose which columns and overview entries are visible. |
| **Download controls** | Configure concurrent downloads, connections per file, and speed limits. |
| **Integrity and repair** | Check available source checksums and use matching PAR2 recovery files to verify and repair damaged data. |
| **Archive workflows** | Extract archives automatically or manually, handle multipart archives, try saved passwords, and choose whether to keep or remove source archives. |
| **Appearance** | Dark and White Mode, separate accent colors for each theme, and an English or German interface. |

<h2 align="center">Download</h2>

Get [**setup.exe**](https://github.com/Biiitz/Reepax/releases/latest/download/setup.exe) from the [latest release](https://github.com/Biiitz/Reepax/releases/latest).

Reepax currently supports **Windows 10 and 11 on x64 systems**. The release installer includes the .NET runtime. The integrated browser also requires the **WebView2 Runtime** to be installed.

Per-user installations use Velopack for updates. The installer remains compatible with the update button in earlier versions. Updates use a verified delta package when available and fall back to a verified full package when needed. System-wide installations continue to update through the installer.

<h2 align="center">Local data</h2>

Settings, download state, and saved archive passwords are stored on your PC with Windows account protection. Diagnostic file logging can be enabled or disabled in the app settings.

<h2 align="center">Development</h2>

Bug reports, interface improvements, and pull requests are welcome. Read the [contribution guide](CONTRIBUTING.md) before submitting changes. For bugs, include the app version, steps to reproduce the problem, and the expected result; remove private information from any attachments.

<details>
<summary>Build and run from source</summary>

Use Windows with the .NET 8 SDK and the WebView2 Runtime installed. From the repository root:

```powershell
dotnet build Reepax/Reepax.csproj -c Release
dotnet run --project Reepax/Reepax.csproj
dotnet test Reepax.Tests/Reepax.Tests.csproj
```

The release workflow runs the full test suite and installer checks before publishing.

</details>

<div align="center">

  <h2>License</h2>

  <p>Reepax's original code is licensed under the <a href="LICENSE">MIT License</a>.<br />Third-party components retain their respective licenses.</p>

  <p>
    <a href="legal/DISCLAIMER.md">Disclaimer</a>
    &nbsp;&middot;&nbsp; <a href="legal/THIRD_PARTY_LICENSES.md">Third-party licenses and notices</a>
  </p>

</div>
