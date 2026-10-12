## 🤝 Contributing to Reepax 

**Every contribution makes Reepax better and faster for everyone!** Whether you want to optimize network throughput, improve browser interception, squash a bug, polish the UI, or improve translations—we welcome your help with open arms.

---

## 👥 Contributors in the App

We believe community contributions should be recognized directly inside the application, not just in git commit logs:

- **Merged Pull Requests**: Opening a Pull Request alone does not automatically make you a contributor. Only once your PR has been reviewed, approved, and successfully **merged** into the repository (officially recognized by GitHub as a repository contributor) will you be permanently added to the in-app list.
- **What is displayed**: Once merged, your GitHub name will be featured directly in the **"Contributors"** grid inside the **"About"** tab of the Reepax desktop app, linking straight to your GitHub profile.

---

## 💡 How You Can Help

You don't need to be a C# guru to contribute. Here are areas where you can get involved:

- **🚀 Download Engine & Network Core**: Multi-connection segmented downloading, buffer pooling, bandwidth throttling, socket management, and resume reliability.
- **🛡️ AdBlock & Native Security**: Refining and optimizing the native Rust AdBlock engine (`native/reepax_adblock.dll`), URL filter rules, and connection safety checks.
- **🌐 Browser Integration**: Browser extension communication via Native Messaging (`BrowserExtensionService`), link capture, and download interception.
- **📦 Archives & Integrity**: Archive extraction (ZIP, RAR, 7z) via SharpCompress and PAR2 parity verification/repair.
- **🎨 UI & UX Polish**: WPF layout refinements, modern dark theme styling, smooth animations, high-DPI scaling, and keyboard shortcuts.
- **🌍 Translations**: Help translate Reepax into additional languages or refine existing strings in `Reepax/Services/Localization/` (`Strings_de.cs`, `Strings_en.cs`).
- **🐛 Bug Reports & Reproductions**: Open detailed issues with steps to reproduce, logs, or diagnostic information.
- **📚 Documentation**: Improving technical documentation, architecture overviews, and build guides.

---

## 🛠️ Development Setup

### Prerequisites
- **Windows 10 / 11** (x64)
- **[.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)** or later
- Visual Studio 2022 (with *.NET desktop development* workload), Visual Studio Code (with C# Dev Kit), or JetBrains Rider.

### 1. Fork & Clone
```bash
git clone https://github.com/Biiitz/Reepax.git
cd Reepax
```

### 2. Build the Project
```powershell
dotnet build Reepax.slnx -c Release
```

### 3. Run the Test Suite
Reepax includes an automated test suite (1,000+ unit tests) covering engine logic, queue persistence, archive extraction, and rate limiters:
```powershell
dotnet test Reepax.slnx -c Release
```

---

## 🚀 Submitting a Pull Request

1. **Create a branch**:
   ```bash
   git checkout -b feature/my-improvement
   ```
2. **Commit your changes**:
   Write clear, concise commit messages in English (e.g., `Optimize chunk allocation in DownloadEngine`, `Fix status filter click responsiveness`).
3. **Verify tests pass**:
   ```powershell
   dotnet test Reepax.slnx -c Release
   ```
4. **Push and open a PR**:
   Push to your fork and open a Pull Request against the `main` branch.
5. **Get recognized!**:
   Once merged, your GitHub name and profile link will be added to the in-app **Contributors** section!

---

## 💬 Questions & Community

Have an idea or want to discuss an architectural change before implementing it?
- Open an issue in [GitHub Issues](https://github.com/Biiitz/Reepax/issues).
- Start a discussion or suggest a feature.

Thank you!
