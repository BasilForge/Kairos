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
  <a href="https://discord.gg/QgqsGEWzaF"><img src="https://img.shields.io/badge/Discord-Join-5865F2?style=for-the-badge&logo=discord&logoColor=white" alt="Discord"></a>
  <img src="https://img.shields.io/badge/license-GPL--2.0-3A3130?style=for-the-badge" alt="License: GPL-2.0">
  <img src="https://img.shields.io/badge/languages-8-3A3130?style=for-the-badge" alt="8 languages">
</p>

<p align="center">
  <b>English</b> ·
  <a href="docs/README.ar.md">العربية</a> ·
  <a href="docs/README.zh-Hans.md">简体中文</a> ·
  <a href="docs/README.ru.md">Русский</a> ·
  <a href="docs/README.es.md">Español</a> ·
  <a href="docs/README.pt-BR.md">Português (Brasil)</a> ·
  <a href="docs/README.de.md">Deutsch</a> ·
  <a href="docs/README.fr.md">Français</a>
</p>

https://github.com/user-attachments/assets/c7afb475-f4a2-4356-acc5-327935e879e2

## Why Kairos

- **Never miss the moment.** Kairos keeps the last 15 seconds to 20 minutes (5 by default) on your disk or in RAM. Press `Alt+F10` and it becomes a clip.
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

- **Capture:** full screen, up to 5 apps (the one in front is recorded), or games (picked up automatically, stretched and exclusive fullscreen too), up to 4K at 30 to 240 fps.
- **Instant replay:** up to 20 minutes, on your disk (SSD or HDD) or in RAM, a quick clip beside it, and if you like a fresh start after each save.
- **Automatic quality:** Kairos picks the encoder, size, frame rate and quality your PC records best with, and steps down by itself if frames are skipped.
- **Encoding:** AV1, HEVC or H.264 on NVIDIA (NVENC), AMD (AMF) or Intel (Quick Sync); if one cannot start, the next one records, down to x264.
- **Audio:** a mixer with a row per source (game, Discord, music, microphone, any app or device), each with its own level and, if you like, its own track.
- **Camera:** your webcam or phone in a corner (square or round) or on top of a 9:16 picture, and if you like saved on its own.
- **Recording:** start, pause and stop with hotkeys; a paused recording stays one file.
- **Screenshots** with their own hotkey, saved beside your clips.
- **Library:** clips and screenshots by day, filtered by game; drag a clip straight into Discord; a folder size limit; save to OneDrive, Dropbox or Google Drive and free the local copies.
- **Editor:** cut and join, speed and sound per part (every track), zoom into an area, text in many fonts and styles, auto captions, drawing, shapes, blur, filters, and crop for screenshots.
- **Auto captions:** Whisper on your graphics card. The engine and model are downloaded once, after you agree, and can be removed in Settings.
- **Share:** X, TikTok, Instagram, or all of them, through each site's own page; Copy made to fit Discord's 10, 50 or 500 MB; trim on a frame strip and crop to 9:16; copies kept in your clips folder's Shared.
- **Help:** Save a report on the Status page puts the logs and settings in a zip on your Desktop, to send on Discord.
- **Eight languages:** English, Arabic, Chinese, Russian, Spanish, Portuguese, German and French. The first run follows your Windows language.
- **Quiet:** starts with Windows in the tray, with no pop-ups or sounds unless you turn them on.

</details>

## Privacy

- No telemetry, no analytics, no Kairos account.
- Kairos goes online only to check GitHub for a newer version once a day and download it (you can turn that off), to post what you share through each site's own page, and to download auto captions once, after you agree (the engine from GitHub, the model from Hugging Face).
- Your clips stay in your clips folder (synced only if you pick a OneDrive, Dropbox or Google Drive folder), and your settings and logs on your PC. Smart Save and auto captions listen on your PC only, and your Smart Save phrase is recorded there only.
- Site sign-ins stay in Kairos's own browser on your PC (Microsoft Edge WebView2, protected by Windows as in Edge), never in Kairos's settings or logs. Signing out of a site also signs out of the Google, Apple or Facebook account used for it.
- **Save a report** holds Kairos's logs and settings without your Windows user name or window titles, and never sign-ins or clips.
- Found a security problem? Please report it privately: see [SECURITY.md](SECURITY.md).

## FAQ

**Is Kairos safe with anti-cheat?**

Kairos never reads or changes a game's memory. To capture a game that fills the screen (in every mode, so a stretched or exclusive fullscreen game is never black), it uses the same signed, anti-cheat-compatible capture hook as OBS Studio, which Riot Vanguard, Easy Anti-Cheat and BattlEye allow. If a game ever gives no picture through the hook, Kairos records the screen instead by itself, so a clip is never black.

**Does a yellow border show while recording?**

Not on Windows 11. On Windows 10, Windows itself draws a yellow border around the app in **Apps** mode and gives apps no way to hide it; **Full screen** and **Games** never show one.

**My clips stutter above 60 fps.**

A clip can't be smoother than your screen or your game: recording 144 fps on a 60 Hz screen repeats frames unevenly. Kairos says so under the frame rate, and **Automatic** in Quality never records above your screen's rate. 60 suits YouTube, X, TikTok and Instagram.

**Something doesn't work. What do I send?**

Press **Save a report** on the Status page: a zip with Kairos's logs and settings lands on your Desktop. Send it in our [Discord](https://discord.gg/QgqsGEWzaF) or in a GitHub issue.

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

## License

Kairos is free software under the [GNU General Public License v2.0 or later](LICENSE), the same license as libobs. Every release includes its complete source code. Third-party components and their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

<sub>*Kairos* (καιρός) is Greek for *the right moment*.</sub>
