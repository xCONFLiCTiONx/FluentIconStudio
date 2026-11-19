# FluentIconStudio

## Credits & Acknowledgments

Special thanks and shout-out to **Microsoft Design** and the creators & contributors of [microsoft/fluentui-system-icons](https://github.com/microsoft/fluentui-system-icons)!

- **Official Repository**: [microsoft/fluentui-system-icons on GitHub](https://github.com/microsoft/fluentui-system-icons)
- **License**: Licensed under the [MIT License](https://github.com/microsoft/fluentui-system-icons/blob/main/LICENSE) by Microsoft Corporation.

The Fluent System Icons collection is a beautifully crafted, comprehensive set of friendly, modern, and consistent vector icons designed for Windows and modern applications. FluentIconStudio is built to complement this incredible open-source icon system by making it easy to search, edit, preview, and export high-resolution assets for desktop and modern apps.

## Features & Functionality

- **In-App Official Fluent Pack Downloader**: Download and load the complete official [microsoft/fluentui-system-icons](https://github.com/microsoft/fluentui-system-icons) repository (~10,000+ SVG icons) directly from GitHub into local AppData with live progress tracking.
- **Instant Search & In-Memory Indexing**: Sub-millisecond indexed search across tens of thousands of icons with debounced typing.
- **High-Contrast Canvas & Icon Color Modes**: Easily toggle between **White Icon / Dark Canvas** and **Black Icon / Light Canvas** with instant SVG XML recoloring.
- **Auto 512×512 Viewer Scaling**: Select any icon to automatically load and format its SVG XML code to 512×512 with proper `viewBox` scaling.
- **Android App Icon Pack Export (ZIP)**: Generates complete Android project resource structures (`res/mipmap-mdpi` through `xxxhdpi`, `ic_launcher_round.png`, `ic_launcher_foreground.png`, 512px Play Store Web Master, and v26+ Adaptive Icon XMLs).
- **Chrome Extension & PWA Icon Pack Export (ZIP)**: Generates 16, 32, 48, 128, and 512 px PNGs packaged with a Manifest V3 compliant `manifest.json`.
- **Windows ICO Export**: Export multi-image Windows `.ico` files with standard 16, 24, 32, 48, 64, 128, and 256 px entries plus a 512px master PNG.
- **Non-Intrusive Status Reporting**: All actions, errors, and progress report cleanly in the status bar without modal message box popups.
- **SVG Source Code Editor**: Edit root properties (`width`, `height`, `viewBox`, `fill`, `stroke`) and raw XML source with live Apply & Preview.
- **Window Position Persistence**: Automatically saves and restores window bounds and maximized state across sessions.

## Requirements

- Windows 10/11
- .NET 10 SDK
- Internet access on first build to restore NuGet dependencies

## Build and run

Open `FluentIconStudio.sln` in Visual Studio 2022 with a current .NET 10 SDK, or run:

```powershell
dotnet restore .\FluentIconStudio.sln
dotnet build .\FluentIconStudio.sln -c Release
dotnet run --project .\src\FluentIconStudio\FluentIconStudio.csproj
```

## Adding icon assets

- **Download Fluent Pack (GitHub)**: Use the built-in **Download Fluent Pack (GitHub)** button to download and extract the complete official [microsoft/fluentui-system-icons](https://github.com/microsoft/fluentui-system-icons) repository (~10,000+ SVG icons) directly into local AppData.
- **Open icon folder…**: Choose any custom local folder containing SVGs to browse local icon sets. Keep the upstream license and copyright notices alongside any icon assets you redistribute.
