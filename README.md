<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/media/logo-dark.png">
    <img src="docs/media/logo-light.png" alt="Kairos" width="360">
  </picture>
</p>

<h3 align="center">Instant replay for Windows games.</h3>

<p align="center">
  Press a key or say your phrase, and the last five minutes are saved.<br>
  Free, open source, offline, and so light you'll forget it's on.
</p>

<p align="center">
  <a href="https://github.com/BasilForge/Kairos/releases/latest"><img src="https://img.shields.io/badge/Download-Windows%2010%20%7C%2011-F48BA4?style=for-the-badge&logo=windows&logoColor=white" alt="Download for Windows"></a>
  <img src="https://img.shields.io/badge/license-GPL--2.0-3A3130?style=for-the-badge" alt="License: GPL-2.0">
  <img src="https://img.shields.io/badge/languages-7-3A3130?style=for-the-badge" alt="7 languages">
</p>

<p align="center">
  <b>English</b> ·
  <a href="docs/README.ar.md">العربية</a> ·
  <a href="docs/README.zh-Hans.md">简体中文</a> ·
  <a href="docs/README.ru.md">Русский</a> ·
  <a href="docs/README.es.md">Español</a> ·
  <a href="docs/README.pt-BR.md">Português (Brasil)</a> ·
  <a href="docs/README.de.md">Deutsch</a>
</p>

https://github.com/user-attachments/assets/c7afb475-f4a2-4356-acc5-327935e879e2

## Why Kairos

- **Never miss the moment.** Kairos keeps the last 15 seconds to 20 minutes in memory (5 by default). Press `Alt+F10` and it becomes a clip.
- **Smart Save.** Record a phrase of your own, in any language, then just say it to save a clip or take a screenshot. It runs offline.
- **Light.** It records on your graphics card. Measured while a game ran at 1440p60 AV1: 0.4% CPU, no dropped frames.
- **Edit in place.** Cut, change the speed, add text, draw, blur, and make auto captions on your own PC.
- **Share in one press.** Post to X, TikTok and Instagram, or all three at once with one caption, or copy the clip and paste it anywhere.
- **Private.** No account, no telemetry. Your clips stay on your PC until you share them.

## Screenshots

<table>
  <tr>
    <td width="50%"><img src="docs/media/screens/en/status.webp" alt="Status"><p align="center"><sub>Instant replay, recording and screenshots at a glance</sub></p></td>
    <td width="50%"><img src="docs/media/screens/en/library.webp" alt="Library"><p align="center"><sub>Every clip and screenshot, by day and by game</sub></p></td>
  </tr>
  <tr>
    <td width="50%"><img src="docs/media/screens/en/editor.webp" alt="Editor"><p align="center"><sub>The editor, with auto captions made on your PC</sub></p></td>
    <td width="50%"><img src="docs/media/screens/en/share.webp" alt="Share"><p align="center"><sub>Post to X, TikTok and Instagram for you</sub></p></td>
  </tr>
  <tr>
    <td width="50%"><img src="docs/media/screens/en/smart-save.webp" alt="Smart Save"><p align="center"><sub>Smart Save: say your phrase to save</sub></p></td>
    <td width="50%"><img src="docs/media/screens/en/accounts.webp" alt="Accounts"><p align="center"><sub>Sign in once; sign-ins stay on your PC</sub></p></td>
  </tr>
</table>

## Download

1. Download **Kairos-Setup.exe** from the [latest release](https://github.com/BasilForge/Kairos/releases/latest).
2. Run it and follow the steps: pick your language and the folder (no administrator rights needed), and whether you want a desktop shortcut and Kairos to start with Windows.
3. Play, then press `Alt+F10` when something happens.

Windows may warn about an unknown publisher (the installer is not signed yet): choose **More info**, then **Run anyway**.
Updates come from inside the app, and never during a recording. Uninstalling keeps your clips and settings.

**Needs:** Windows 10 (2004 or newer) or Windows 11, 64-bit. A graphics card with a video encoder (NVIDIA, AMD or Intel) is recommended.

## Features

<details>
<summary>Everything Kairos does</summary>

- **Capture:** full screen, one window, or games (picked up automatically), up to 4K at 30 to 240 fps.
- **Encoding:** AV1, HEVC or H.264 on NVIDIA (NVENC), AMD (AMF) or Intel (Quick Sync), with x264 as a fallback.
- **Audio:** system sound and microphone, optionally on separate tracks.
- **Recording:** start, pause and stop with hotkeys; a paused recording stays one file.
- **Screenshots** with their own hotkey, saved beside your clips.
- **Library:** clips and screenshots by day, filtered by game, with a folder size limit that sends the oldest to the Recycle Bin.
- **Editor:** cut and join, speed and sound per part, text in many fonts and styles, auto captions, drawing, shapes, blur, filters, and crop for screenshots.
- **Auto captions:** Whisper on your graphics card. The engine and model are downloaded once, after you agree, and can be removed in Settings.
- **Share:** X, TikTok, Instagram, or all of them, through each site's own page; trim on a frame strip and crop to 9:16.
- **Seven languages:** English, Arabic, Chinese, Russian, Spanish, Portuguese and German. The first run follows your Windows language.
- **Quiet:** starts with Windows in the tray, with no pop-ups or sounds unless you turn them on.

</details>

## Privacy

- No telemetry, no analytics, no Kairos account.
- Kairos goes online only when you ask: to post what you share, to download auto captions once, and to check this page for a newer version once a day (you can turn that off).
- Smart Save and auto captions listen on your PC only.

## Build from source

<details>
<summary>For developers</summary>

Kairos is a .NET 10 app (WPF and WinForms) that hosts **libobs**, the engine of [OBS Studio](https://obsproject.com), with only the modules it needs, and uses FFmpeg for editing and sharing.

```powershell
.\tools\build-whisper.ps1               # once: the auto captions engine (needs the Vulkan SDK and VS Build Tools)
.\tools\release.ps1                     # the Setup, update files and source zip in releases\ (needs vpk 1.2.161)
dotnet run --project tests\Kairos.Tests # the tests
```

The build copies libobs 32.2.2 from an installed OBS Studio.

</details>

## Linux

A native Linux build lives in `src/Kairos.Linux` (Avalonia UI, X11). It shares the engine, editor logic and view models with the Windows app.

```bash
tools/build-libobs.sh                   # once: libobs 32.2.2 with the Linux capture, PulseAudio/PipeWire and NVENC/x264 plugins
tools/get-ffmpeg.sh                     # once: the FFmpeg 8 libraries (or point KAIROS_FFMPEG at a folder that has them)
tools/build-whisper.sh                  # optional: the auto captions engine (CPU; Vulkan when glslc is installed)
dotnet run --project src/Kairos.Linux   # starts the engine and tray; "kairos --settings" opens the window
dotnet run --project tests/Kairos.Linux.Tests
```

- Games mode follows the focused window; Screen mode captures the desktop (it costs more CPU).
- Share posts to X, TikTok and Instagram through a Chromium-family browser (Brave, Chrome, Chromium, Edge) with a sign-in profile of its own in `~/.local/share/Kairos/WebView`. Without one, Share opens the site in your normal browser. `KAIROS_BROWSER` picks a browser by hand.
- Wayland is not supported yet.

## License

Kairos is free software under the [GNU General Public License v2.0 or later](LICENSE), the same license as libobs. Every release includes its complete source code. Third-party components and their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

<sub>*Kairos* (καιρός) is Greek for *the right moment*.</sub>
