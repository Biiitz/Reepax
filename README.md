<!-- markdownlint-disable-file -->

<p align="center">
  <img src="Reepax/Assets/R.png" alt="Reepax Logo/Banner" width="150" />
</p>

<p align="center">
  <a href="https://github.com/Biiitz/Reepax/actions/workflows/release.yml">
    <img src="https://github.com/Biiitz/Reepax/actions/workflows/release.yml/badge.svg" alt="Build & Release Reepax">
  </a>
  <img src="https://img.shields.io/badge/Tests-800%2B%20Passing-success?style=flat" alt="800+ Passing Tests">
</p>

<p align="center">
  <a href="#overview">Overview</a> •
  <a href="#features">Features</a> •
  <a href="#installation">Installation</a> •
  <a href="#extensions">Extensions</a> •
  <a href="#privacy">Privacy</a> •
  <a href="LICENSE.md">License</a>
</p>

<p align="center">
   <b>Notice:</b> Reepax is an Early/Evolving project provided &ldquo;as is&rdquo;. Please read the <a href="legal/DISCLAIMER.md">Disclaimer</a> before use.
</p>

---

## Overview

**Reepax** is a fast, lightweight download manager for Windows built with **.NET 8** and **WPF**. It streamlines the entire download workflow into a clean, modern interface: accelerating multi-connection transfers, resolving hoster gates and countdowns via an isolated browser sandbox, repairing damaged archives with integrated PAR2, and automatically extracting finished files directly to your drive.

Backed by **800+ automated tests**, the architecture is thoroughly verified for stability, download integrity, and resilient background processing.

---

## Features

### Multi-Stream Downloads
- **Segmented Acceleration**: Splits files into up to 20 parallel HTTP range connections to maximize download speeds, with automatic fallback to single-stream for servers without range support.
- **Reliable Pause & Resume**: Persistent state tracking (`.part.segments`) allows transfers to be paused, resumed, or recovered smoothly after network drops or app restarts.
- **Bandwidth Limiter**: High-precision token-bucket rate limiter provides smooth, jitter-free speed limiting (MB/s) to keep your connection responsive.
- **Smart Queue & Clipboard**: Monitors clipboard links automatically, supports batch imports via `.repx` package files, and sends authentic browser headers with HTTP/2 support.

### Integrated Browser & Shields
- **Isolated WebView2 Sandbox**: Solves hoster countdown gates and interactive captchas in an isolated Chromium window. Once the download stream begins, Reepax captures the authenticated link and closes the browser automatically.
- **High-Performance Content Shield**: Powered by Brave's native `adblock-rust` engine to block intrusive ads, disruptive redirects, overlay traps, and tracking scripts before they load.
- **Chromium Extension Support**: Load unpacked extensions (such as uBlock Origin or custom userscripts) directly into the embedded sandbox.

### PAR2 Repair & Auto-Extract
- **Automated PAR2 Verification & Repair**: Embedded SIMD-accelerated `par2cmdline-turbo` engine checks parity files (`.par2`) and reconstructs damaged or missing archive parts before extraction.
- **Multi-Format Extraction**: Automatically unpacks `.zip`, `.rar`, and `.7z` archives—including multi-part split volumes—upon download completion.
- **Storage-Aware Performance**: Detects physical drive geometry (`IOCTL_STORAGE_QUERY_PROPERTY`) to differentiate SSDs from HDDs, dynamically tuning extraction concurrency to avoid disk thrashing and UI freezes.
- **Safe Part Cleanup**: Option to delete archive parts and parity files automatically or send them safely to the Windows Recycle Bin once unpacked.

### Modern UI & Windows Integration
- **Fluent Dark Interface**: Sleek WPF design with Windows DWM immersive title bars, fluent controls, and system tray minimization.
- **Bilingual Support**: Fully localized in English and German with instant runtime switching.
- **Windows Integration**: Native Explorer file associations for `.repx` packages, drag-and-drop queue import, and DPAPI-encrypted credential storage.

---

## Installation

### Windows Installer (`setup.exe`)
*Recommended for standard desktop usage.*
1. Download **`setup.exe`** from the latest [Releases](https://github.com/Biiitz/Reepax/releases).
2. Run the installer to configure desktop shortcuts, Start Menu entries, and `.repx` file associations.
3. Clean uninstallation is fully supported with optional AppData cleanup.

### Portable Edition (`portable.zip`)
*Ideal for USB drives or running without installation.*
1. Download **`portable.zip`** from the latest [Releases](https://github.com/Biiitz/Reepax/releases).
2. Extract the archive to any folder.
3. Launch **`Reepax.exe`**. All configuration and queue states are stored self-contained in the local `Data` folder.

---

## Extensions

### Loading Browser Extensions
You can load unpacked Chromium extensions into Reepax's embedded WebView2 browser:

1. **Open the Extensions Directory**:
   - Press <kbd>Ctrl</kbd> + <kbd>E</kbd>, or
   - Click the **Folder icon** in the bottom-right status bar, or
   - Navigate to `%LOCALAPPDATA%\Reepax\Extensions` in Windows Explorer.
2. **Install**: Place your unpacked extension folder (the directory containing `manifest.json`) inside the `Extensions` directory.
3. **Restart Reepax**: Restart the application so WebView2 registers the extensions during startup.
4. **Manage**: Installed extensions appear as icons in the top-right toolbar when a browser window opens. Click an icon to open its popup, or right-click to toggle or remove it.

> [!NOTE]
> Due to WebView2 lifecycle requirements, browser extensions are registered when the Chromium environment initializes at application launch. Always restart Reepax after adding or modifying extension folders.

---

## Privacy

- **Zero Telemetry**: Reepax does not track activity, collect usage analytics, or transmit download URLs to external servers.
- **DPAPI-Encrypted Local Storage**: All settings, download queues, and history are stored locally on your machine in `%LOCALAPPDATA%\Reepax` as DPAPI-encrypted files (`settings.json`, `downloads.json`, `history.json`).
- **Configurable Logging**: Rotating diagnostic file logging is disabled by default and can be enabled at any time in the Settings tab.

Developed by [Biiitz](https://github.com/Biiitz).