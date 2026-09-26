<!-- markdownlint-disable-file -->
<p align="center">
  <a href="https://github.com/Biiitz/Reepax/actions/workflows/release.yml">
    <img src="https://github.com/Biiitz/Reepax/actions/workflows/release.yml/badge.svg" alt="Build & Release Reepax">
  </a>
</p>

<p align="center">
  <a href="#overview">Overview</a> •
  <a href="#architecture--pipeline">Architecture</a> •
  <a href="#key-features">Key Features</a> •
  <a href="#installation--portable-usage">Download</a> •
  <a href="#tips--advanced-usage">Tips & Notes</a> •
  <a href="#license">License</a>
</p>

---

## Overview

**Reepax** is a specialized, lightweight download manager for Windows built with **.NET 8** and **C# 12**. Engineered from the ground up as a native alternative to heavyweight tools and outdated software, Reepax delivers sub-second cold starts (< 1s), an idle memory footprint of ~60–90 MB RAM, and saturation-level transfer speeds via multi-stream parallel chunking.

Beyond raw throughput, Reepax orchestrates the entire post-processing lifecycle: from resolving hoster gates inside an isolated WebView2 sandbox and stripping deceptive ad networks, to managing chunk assembly, crash-safe state persistence, and automatically unpacking multi-part `.rar`, `.7z`, and `.zip` archives with real-time physical drive awareness (SSD vs. HDD).

---

## Architecture & Pipeline

Reepax is architected around a streamlined execution pipeline designed for maximum throughput and stability:

1. **Ingestion & Shield Layer**
   - **Multi-Source Link Parsing**: Rapidly ingests links from clipboard monitoring, manual text input, or `.repx` package files.
   - **Isolated Browser Sandbox**: Resolves hoster gates, countdowns, and Cloudflare challenges inside an isolated WebView2 environment.
   - **Ad & Pop-Up Neutralizer**: Strips intrusive ad networks, suppresses hostile redirects, and sniffs authenticated media streams directly into the native download engine.

2. **High-Throughput Multi-Stream Core**
   - **Dynamic TCP Range Workers**: Splits downloads into 1–20 parallel HTTP range segments to maximize network utilization.
   - **Microsecond Token-Bucket Limiter**: Smooth, jitter-free speed limiting powered by the 1ms Windows Multimedia Timer (`timeBeginPeriod`).
   - **Crash-Resilient State Sync**: File progress and segment maps are persisted in real time to `.part.segments` files for instant resume capability.

3. **PAR2 Auto-Repair Engine**
   - **Reed-Solomon Parity Reconstruction**: Integrated AVX2/AVX-512 SIMD-accelerated `par2cmdline-turbo` engine.
   - **Pre-Extraction Integrity Healing**: Automatically checks and repairs damaged archive parts before unrar/unzip processes begin.
   - **Missing Block Intelligence**: Accurately assesses recovery capacity and warns if additional blocks are required.

4. **Hardware-Aware Extraction**
   - **Drive Geometry Detection**: Dynamically inspects physical storage (`IOCTL_STORAGE_QUERY_PROPERTY`) to detect SSD vs. HDD media.
   - **Adaptive Unpacking**: Auto-tunes extraction concurrency and buffer allocations for `.zip`, `.rar`, and `.7z` multi-part archives to prevent disk stalls.
   - **Safe Part Cleanup**: Automatically cleans up archive parts and `.par2` parity sets or sends them safely to the Windows Recycle Bin once unpacked.

---

## Key Features

### High-Speed Multi-Stream Engine
- **Multi-Connection Segmented Downloads**: Dynamically splits large files into up to 20 parallel HTTP range segments, saturating available network bandwidth and minimizing connection latency.
- **Microsecond Token-Bucket Limiter**: Smooth, jitter-free global bandwidth limiter (MB/s) utilizing the 1ms Windows Multimedia Timer (`timeBeginPeriod`) to prevent network congestion without transfer oscillation.
- **High-Throughput Stream Pipeline**: Engineered with 256 KB memory buffers, async I/O completion ports, and HTTP/2 stream multiplexing with 16 MB flow control windows.
- **Dynamic Header Emulation**: Inspects and clones the host system's authentic Edge/Chrome User-Agent and sends complete legitimate browser headers (`Sec-Ch-Ua`, `Sec-Fetch-*`, Brotli/GZip decompression) with anti-rate-limiting request jitter.

### Automated PAR2 Auto-Repair Engine
- **SIMD-Accelerated Repair**: Embedded high-performance `par2cmdline-turbo` engine utilizing AVX2 and AVX-512 vector extensions for ultra-fast parity verification and reconstruction.
- **Automated Pre-Extraction Healing**: Scans `.par2` and `.volXX+YY.par2` volume files automatically upon download completion. If any archive parts are corrupted or damaged during transfer, Reepax reconstructs the missing or broken slices with mathematical precision before extraction begins.
- **Intelligent Block Calculation**: Accurately computes available vs. needed recovery blocks in real time and reports exact progress and missing counts.
- **Clean Post-Repair Lifecycle**: Automatically cleans up parity files and temporary backup files alongside archive parts upon successful verification and extraction.
- **On-Demand Package Verification**: Trigger manual PAR2 verification and repair directly from the package context menu at any time.

### Hardened Ad & Redirect Shield
- **Sandboxed WebView2 Browser Pool**: Captcha gates and countdown pages open in isolated, floating WebView2 instances. As soon as the final download stream fires, Reepax intercepts the authenticated request, transfers it to the native engine, and closes the browser window automatically.
- **Domain & Pattern Filtering**: Built-in rule parser filters out ~170 known intrusive advertising, pop-up, tracker, and deceptive ad networks.
- **Hostile Redirect Containment**: Protects users from deceptive 30x multi-hop redirect chains and foreign domain hops (eTLD+1 inspection) while safely permitting CDN mirrors and legitimate user interactions.
- **Pop-up & Overlay Neutralization**: Virtualizes `window.open`, blocks unprompted tab creation, strips floating overlay traps via `MutationObserver`, and auto-denies browser permission requests.

### Hardware-Aware Automated Extraction
- **Automated Archive Extraction**: Seamless extraction for `.zip`, `.rar`, and `.7z` archives upon completion (including complex split volumes like `.part01.rar`, `.z01`, etc.), disabled by default for full user control.
- **Physical Drive Geometry Detection**: Dynamically queries physical disk drive geometry (`IOCTL_STORAGE_QUERY_PROPERTY`) to differentiate between Solid State Drives (SSDs) and Mechanical Hard Drives (HDDs), adjusting extraction thread priority and concurrency to avoid system lockups.
- **Live Pipeline Indicator**: Visual step indicator with real-time status pulses: *PAR2-Repair → Extracting → Cleanup → Done*.
- **Recycle Bin Protection**: Safely cleans up archive files after extraction or moves them to the Windows Recycle Bin depending on user preference.

### Security & Windows Integration
- **DPAPI Protected Storage**: Sensitive configuration details and credentials are encrypted at rest using the native Windows Data Protection API (`ProtectedData`).
- **File Association (`.repx`)**: Seamlessly registers `.repx` (and legacy `.sdlr`) package formats with Windows Explorer for instant double-click and drag-and-drop queue imports.
- **Native Fluent Theming**: Dark & Light modes with Windows DWM Immersive Dark Mode title bars and an interactive 2D color picker.
- **Bilingual Interface**: Fully localized in **English** and **German** with instant runtime switching.

<details>
<summary><b>Detailed Technical Specifications</b></summary>

| Parameter | Specification | Description |
|---|---|---|
| **Target Framework** | `.NET 8.0-windows` | Self-contained x64 deployment |
| **UI Framework** | WPF (Windows Presentation Foundation) | Hardware-accelerated DirectX rendering pipeline |
| **I/O Buffer Size** | 256 KB | Ring buffers optimized for NVMe SSD and high-speed networks |
| **HTTP Pipeline** | `SocketsHttpHandler` | HTTP/2 multiplexing, 16 MB connection stream window |
| **Timer Precision** | WinMM 1ms (`timeBeginPeriod`) | Microsecond token-bucket scheduler |
| **Archive Formats** | ZIP, RAR, 7Z, TAR, GZ | Multi-part split archive support via SharpCompress |
| **Browser Runtime** | Microsoft Edge WebView2 (Chromium) | Isolated profile sandbox with extension support |
| **Persistence** | System.Text.Json + DPAPI | Atomic file writes with `.part.segments` sidecar sync |

</details>

---

## Installation & Portable Usage

### Option 1: Windows Installer (`setup.exe`)
For a clean, standard desktop installation:
1. Download `setup.exe` from the latest Releases.
2. Run the installer to set up Start Menu shortcuts, desktop icons, and `.repx` package file associations.
3. Clean uninstallation is fully supported with optional AppData cleanup prompt.

### Option 2: Portable Edition (`portable.zip`)
For USB drives or running without administrative privileges:
1. Download `portable.zip` from the latest Releases.
2. Extract the archive into any folder of your choice.
3. Launch `Reepax.exe`. All configurations, settings, and queues are stored safely within your user profile (`%LOCALAPPDATA%\Reepax`).

---

## Tips & Advanced Usage

### Browser Extensions (WebView2)
Reepax includes an isolated Chromium WebView2 runtime to automatically navigate and resolve hoster countdown gates and interactive confirmation challenges. You can enhance the browser environment with your own unpacked Chromium extensions (such as uBlock Origin, privacy enhancers, or custom automation scripts):

1. **Opening the Extensions Directory**:
   - Press the shortcut <kbd>Ctrl</kbd> + <kbd>E</kbd>
   - Or click the **Folder icon button** in the bottom-right status bar (positioned directly next to the Theme and Settings buttons)
   - Or open `%LOCALAPPDATA%\Reepax\Extensions` directly in Windows Explorer
2. **Installing an Extension**:
   - Extract and place your **unpacked extension folder** (must directly contain its `manifest.json` file) into this directory.
3. **Using & Managing Extensions**:
   - When an isolated browser window opens (e.g. to solve a captcha or countdown gate), all installed extensions are displayed as clickable icons in the browser window's top-right toolbar.
   - Click an extension's icon to view its popup or options page, or right-click it to quickly toggle it on/off or remove it.

> [!IMPORTANT]
> **Application Restart Required**: Due to Microsoft WebView2 architectural lifecycle constraints, browser extensions are registered exclusively when the Chromium environment is first initialized at application launch. If you add, remove, or modify extension folders while Reepax is already running, **you must restart Reepax** for WebView2 to discover and activate the changes.

### Multi-Part Archives & Modular Packages
When downloading multi-part archive packages with optional components (e.g. modular software components, extra localizations, media packs):
- Official update patches frequently verify the SHA-256 / MD5 checksums of all standard installation files.
- If optional files are missing, subsequent patch installers may abort with verification errors.
- If you intend to install future official updates, it is recommended to download all package parts rather than skipping components.

### Hardware-Aware Archive Extraction
Reepax queries physical drive geometry via the Windows storage control layer (`IOCTL_STORAGE_QUERY_PROPERTY`) to determine whether the target directory resides on a Solid State Drive (SSD) or a Mechanical Hard Drive (HDD):
- **SSDs (NVMe / SATA)**: Operates with fully parallelized multi-threading and deep ring buffers to maximize gigabit decompression throughput.
- **HDDs (Mechanical)**: Concurrency is dynamically throttled to prevent head thrashing, severe queue depth buildup, and system responsiveness drops during heavy unpack operations.

---

## Testing & Quality Assurance

Reepax is backed by a robust suite of **640 automated xUnit unit and integration tests** ensuring stability across all core subsystems:

```bash
# Clone the repository
git clone https://github.com/Biiitz/Reepax.git
cd Reepax

dotnet test Reepax.slnx
```

Test coverage includes:
- **Download Engine**: Chunk slicing, TCP range headers, connection throttling, and token-bucket accuracy.
- **Crash Recovery**: State persistence, `.part.segments` serialization, and byte count calculation on restart.
- **Archive Extraction**: Multi-part `.rar`/`.zip`/`.7z` extraction, path traversal safety, and drive hardware detection.
- **Shield & Security**: Redirect prevention, ad-block regex parsing, DPAPI encryption, and origin watermarks.
- **UI & Localization**: Dynamic English/German switching, model aggregation, and MVVM command dispatching.

---

## Privacy & Local Storage

- **Zero Telemetry**: Reepax does not collect user data, track activity, or transmit download URLs to external analytics servers.
- **Local Persistence**: All configuration parameters, download queues, and historical statistics are stored locally on your device in standard JSON files (`settings.json`, `downloads.json`, `history.json`).
- **Configurable Logging**: Diagnostic file logging can be completely disabled in the Settings tab at any time.

---

## License

This software and its source code are licensed under the personal, non-commercial **Reepax License** — see the [LICENSE.md](LICENSE.md) file for details. Redistribution, modifying derivative works, commercial use, and public re-uploading are strictly prohibited.

Bundled third-party components (such as `par2cmdline-turbo` / `par2.exe` and `adblock-rust` / `reepax_adblock.dll`) are distributed under their respective open-source licenses (GNU GPL v2.0 or later, MPL-2.0) — see [LICENSE.md](LICENSE.md) for details.

Developed by [Biiitz](https://github.com/Biiitz).
