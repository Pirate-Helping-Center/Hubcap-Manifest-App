# Original Upload by Hubcap-manifest
## We are sadly not abel to fix the CludRedirect Patch untill we are provided an working DLL!
If you want to use the Patches please use [CloudRedirect](https://github.com/Selectively11/CloudRedirect/releases/latest) or wait untill we have an working version
Get the latest updated version here: [Updated](https://github.com/Pirate-Helping-Center/PHC-Manifest-App/releases/latest)


# PHC Manifest App

<div align="center">

**A comprehensive Steam depot and manifest management tool**

[Latest official Release](https://github.com/Pirate-Helping-Center/Hubcap-Manifest-App-Arcive/releases/latest)

</div>

---

## Description

PHC Manifest App is a powerful Windows desktop application for managing Steam game depots and manifests. Built with .NET 8 and WPF, it features a modern Steam-inspired interface with two operation modes: **SteamTools** (Lua scripts) and **DepotDownloader** (direct game file downloads).

## Key Features

### Two Operation Modes
- **SteamTools Mode**: Install and manage Lua scripts for Steam games via manifest downloads
- **DepotDownloader Mode**: Download actual game files directly from Steam's CDN with language and depot selection

### Store & Downloads
- **Manifest Library**: Browse and search 1000+ games from hubcapmanifest.com with pagination
- **One-Click Downloads**: Download game manifests with automatic depot key lookup
- **Language & Depot Selection**: Fine-grained control over what gets downloaded
- **Progress Tracking**: Real-time download progress with speed and ETA display
- **Auto-Installation**: Automatically install downloads upon completion
- **Download History**: Track all downloads by mode with total size statistics
- **Workshop Downloader**: Download Steam Workshop items via the manifest API

### Fix Game (Goldberg Emulator Integration)
- **Automatic Goldberg Application**: After DepotDownloader downloads, automatically applies Goldberg Steam Emulator so games are ready to play
- **SteamStub DRM Unpacker**: Unpacks SteamStub DRM protection from executables (7 variant unpackers)
- **DRM Detection**: Detects DRM types and blocks unsupported protection (e.g. Denuvo)
- **Full Emulator Config Generation**: Auto-generates achievements, stats, DLC, and cloud save directories from Lua files and PICS data
- **Launch Config Selection**: Picks the correct executable and arguments from Steam's PICS data
- **ColdClient Mode**: Loader injection support for games that need it

### Library Management
- **Multi-Source Library**: View Lua scripts and Steam games in a unified interface
- **List/Grid View Toggle**: Switch between compact list (with stats) and detailed grid views
- **Image Caching**: SQLite database caching with in-memory bitmap caching
- **Search & Sort**: Filter by name with multiple sorting options
- **Batch Operations**: Bulk enable/disable auto-updates
- **Export Luas**: Backup all Lua files in one zip

### Cloud Saves
- **CloudRedirect Integration**: Full cloud save system with provider support (Dropbox, OneDrive, Google Drive)
- **Cloud Dashboard**: Monitor and manage cloud save status
- **App Pinning**: Pin games to sync across devices
- **Cloud Cleanup**: Remove orphaned backups

### Integrated Tools
- **DepotDumper**: Extract depot information with 2FA QR code support
- **DepotDownloader**: Download files from Steam CDN with progress tracking
- **Config VDF Extractor**: Extract depot keys from Steam's config.vdf
- **GBE Token Generator**: Generate Goldberg emulator tokens with auto-fetch of achievements, depots, DLCs, and language data

### User Experience
- **11 Themes**: Default, Dark, Light, Cherry, Sunset, Forest, Grape, Cyberpunk, Pink, Pastel, Rainbow
- **Custom Theme Editor**: Full color customization with gradient editor, background/sidebar images, live preview, and export/import via share strings
- **UI Scale Slider**: 70% to 150% scaling
- **DPI Scaling**: PerMonitorV2 support for high-DPI displays
- **Responsive UI**: Adapts to window sizes down to 800x600
- **Auto-Updates**: Three modes - Disabled, Check Only, Auto Download & Install
- **System Tray**: Minimize to tray with quick access menu
- **Toast Notifications**: Native Windows 10+ notifications (can be disabled)
- **Protocol Handler**: `hubcapapp://` URI scheme for one-click actions from browsers
- **Single Instance**: Prevents multiple app instances
- **Settings Backup**: Export and import settings and mod lists

## Installation

### Quick Start

1. Download the latest release from [Releases](https://github.com/Pirate-Helping-Center/PHC-Manifest-App/releases/latest)
2. Run `PHCManifestApp.exe`

**That's it!** No installation required. Self-contained single-file executable with all dependencies embedded.

### Requirements

- Windows 10 version 1903 or later
- ~200MB disk space
- Internet connection for downloading depots

### First Launch

On first launch, the app will:
- Create settings in `%AppData%\PHCManifestApp`
- Detect your Steam installation automatically
- Create local SQLite database for library caching

## Configuration

Settings are stored in `%AppData%\PHCManifestApp` and include:

| Category | Options |
|----------|---------|
| Mode | SteamTools, DepotDownloader |
| Downloads | Auto-install, delete ZIP after install, output path |
| Display | Theme selection, custom theme editor, UI scale, window size/position, list/grid view |
| Notifications | Enable/disable toasts and popups |
| Auto-Update | Disabled, Check Only, Auto Download & Install |
| Keys | Auto-upload config keys to community database |
| Cloud | Cloud provider credentials, pinned apps, backup locations |

## URI Scheme

The app registers a `hubcapapp://` protocol handler for quick actions from web browsers or other applications. See [`docs/URI_PROTOCOL.md`](docs/URI_PROTOCOL.md) for full documentation.

| URL Format | Action |
|------------|--------|
| `hubcapapp://download/{appId}` | Download manifest for the specified App ID |
| `hubcapapp://install/{appId}` | Install a previously downloaded game |
| `hubcapapp://download/install/{appId}` | Download and install in one step |

## Technology

- .NET 8.0 with WPF
- Self-contained single-file executable (win-x64)
- SteamKit2 for Steam protocol integration
- SQLite for local caching
- protobuf-net for binary serialization
- Windows Toast Notifications
- CommunityToolkit.Mvvm for MVVM architecture

## Credits

### Integrated Tools
- [DepotDumper](https://github.com/NicknineTheEagle/DepotDumper) by NicknineTheEagle
- [DepotDownloader](https://github.com/SteamRE/DepotDownloader) by SteamRE
- [Steamless](https://github.com/atom0s/Steamless) by atom0s (SteamStub unpacker variants)
- [Goldberg Steam Emulator](https://gitlab.com/Mr_Goldberg/goldberg_emulator) by Mr_Goldberg
- [CloudRedirect](https://github.com/Selectively11/CloudRedirect) by Selectively11

---

<div align="center">


[Discord](https://discord.gg/RNNg5TS7h5) | [GitHub](https://github.com/Pirate-Helping-Center/)

</div>
