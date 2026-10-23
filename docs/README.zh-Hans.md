<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="media/logo-dark.png">
    <img src="media/logo-light.png" alt="Kairos" width="360">
  </picture>
</p>

<h3 align="center">为 Windows 游戏打造的即时回放。</h3>

<p align="center">
  按下按键或说出你的口令，最近五分钟就会被保存。<br>
  免费、开源、离线运行，轻巧到你会忘记它在运行。
</p>

<p align="center">
  <a href="https://github.com/BasilForge/Kairos/releases/latest"><img src="https://img.shields.io/badge/Download-Windows%2010%20%7C%2011-F48BA4?style=for-the-badge&logo=windows&logoColor=white" alt="Download for Windows"></a>
  <a href="https://discord.gg/QgqsGEWzaF"><img src="https://img.shields.io/badge/Discord-Join-5865F2?style=for-the-badge&logo=discord&logoColor=white" alt="Discord"></a>
  <img src="https://img.shields.io/badge/license-GPL--2.0-3A3130?style=for-the-badge" alt="License: GPL-2.0">
  <img src="https://img.shields.io/badge/languages-7-3A3130?style=for-the-badge" alt="7 languages">
</p>

<p align="center">
  <a href="../README.md">English</a> ·
  <a href="README.ar.md">العربية</a> ·
  <b>简体中文</b> ·
  <a href="README.ru.md">Русский</a> ·
  <a href="README.es.md">Español</a> ·
  <a href="README.pt-BR.md">Português (Brasil)</a> ·
  <a href="README.de.md">Deutsch</a>
</p>

https://github.com/user-attachments/assets/c7afb475-f4a2-4356-acc5-327935e879e2

## 为什么选择 Kairos

- **不错过任何精彩瞬间。** Kairos 在硬盘或内存中保留最近 15 秒到 20 分钟（默认 5 分钟）。按下 `Alt+F10`，它就成为一个片段。
- **智能保存。** 录下你自己的口令（任何语言都可以），之后只需说出它，即可保存片段或截图。完全离线运行。
- **轻巧。** 使用显卡录制。以 1440p60 AV1 运行游戏时实测：CPU 占用 0.4%，无丢帧。
- **就地编辑。** 剪辑、调速、添加文字、涂画、模糊，并在你自己的电脑上生成自动字幕。
- **一键分享。** 发布到 X、TikTok 和 Instagram，或用同一段文案同时发布到三个平台；也可以复制片段，随处粘贴。
- **注重隐私。** 无需账号，没有遥测。在你分享之前，片段始终留在你的电脑上。

## 截图

<table>
  <tr>
    <td width="50%"><img src="media/screens/zh-Hans/status.webp" alt="即时回放、录制和截图一目了然"><p align="center"><sub>即时回放、录制和截图一目了然</sub></p></td>
    <td width="50%"><img src="media/screens/zh-Hans/library.webp" alt="所有片段和截图，按日期和游戏整理"><p align="center"><sub>所有片段和截图，按日期和游戏整理</sub></p></td>
  </tr>
  <tr>
    <td width="50%"><img src="media/screens/zh-Hans/editor.webp" alt="编辑器，以及在本机生成的自动字幕"><p align="center"><sub>编辑器，以及在本机生成的自动字幕</sub></p></td>
    <td width="50%"><img src="media/screens/zh-Hans/share.webp" alt="替你发布到 X、TikTok 和 Instagram"><p align="center"><sub>替你发布到 X、TikTok 和 Instagram</sub></p></td>
  </tr>
  <tr>
    <td width="50%"><img src="media/screens/zh-Hans/smart-save.webp" alt="智能保存：说出你的口令即可保存"><p align="center"><sub>智能保存：说出你的口令即可保存</sub></p></td>
    <td width="50%"><img src="media/screens/zh-Hans/accounts.webp" alt="只需登录一次，登录信息只保存在你的电脑上"><p align="center"><sub>只需登录一次，登录信息只保存在你的电脑上</sub></p></td>
  </tr>
</table>

## 下载

1. 从[最新版本](https://github.com/BasilForge/Kairos/releases/latest)下载 **Kairos-Setup.exe**。
2. 运行它并按步骤操作：选择语言和安装文件夹（无需管理员权限），以及是否创建桌面快捷方式、是否随 Windows 启动 Kairos。
3. 开始游戏，精彩时刻出现时按下 `Alt+F10`。

Windows 可能会提示未知发布者（安装程序尚未签名）：选择 **更多信息**，然后选择 **仍要运行**。
更新在应用内完成，录制期间绝不会更新。卸载时会保留你的片段和设置。

**系统要求：** Windows 10（2004 或更高版本）或 Windows 11，64 位。建议使用带视频编码器的显卡（NVIDIA、AMD 或 Intel）。

## 功能

<details>
<summary>Kairos 的全部功能</summary>

- **捕获：** 全屏、最多 5 个应用（录制前台的那个）或游戏（自动识别，拉伸和独占全屏也可以），最高 4K，30 到 240 fps。
- **即时回放：** 最长 20 分钟，可存放在硬盘（SSD 或 HDD）或内存中，旁边还有快速片段，也可以在每次保存后重新开始。
- **自动画质：** Kairos 为你的电脑选择最合适的编码器、尺寸、帧率和画质，掉帧时自动降一级。
- **编码：** NVIDIA (NVENC)、AMD (AMF) 或 Intel (Quick Sync) 上的 AV1、HEVC 或 H.264；某个无法启动时自动换下一个，直到 x264。
- **音频：** 混音器为每个来源（游戏、Discord、音乐、麦克风、任意应用或设备）各设一行，可单独调音量，也可各占一条音轨。
- **摄像头：** 网络摄像头或手机画面放在角落（方形或圆形）或 9:16 画面的上方，也可另存为单独文件。
- **录制：** 用快捷键开始、暂停和停止；暂停过的录制仍是一个文件。
- **截图** 有独立的快捷键，与片段保存在一起。
- **素材库：** 按日期显示片段和截图，可按游戏筛选；把片段直接拖进 Discord；文件夹大小上限；保存到 OneDrive、Dropbox 或 Google Drive 并释放本地副本。
- **编辑器：** 剪切与拼接、每段的速度和声音（所有音轨）、放大某块区域、多种字体和样式的文字、自动字幕、绘图、形状、模糊、滤镜，以及截图裁剪。
- **自动字幕：** 在显卡上运行 Whisper。引擎和模型在你同意后只下载一次，可在设置中删除。
- **分享：** 通过各网站自己的页面发到 X、TikTok、Instagram，或一次全发；“复制”可压到 Discord 的 10、50 或 500 MB；在帧条上裁剪并裁成 9:16；副本保存在剪辑文件夹的 Shared 中。
- **帮助：** 在“状态”页点“保存报告”，日志和设置会打包成 zip 放到桌面，方便在 Discord 发送。
- **七种语言：** 英语、阿拉伯语、中文、俄语、西班牙语、葡萄牙语和德语。首次运行时跟随 Windows 语言。
- **安静：** 随 Windows 启动并驻留在托盘中；除非你开启，否则没有弹窗和声音。

</details>

## 隐私

- 没有遥测，没有数据分析，也没有 Kairos 账号。
- Kairos 只在这些时候联网：每天在 GitHub 上检查一次新版本并下载（可以关闭）、通过各网站自己的页面发布你分享的内容，以及在你同意后一次性下载自动字幕（引擎来自 GitHub，模型来自 Hugging Face）。
- 你的片段留在你的片段文件夹里（只有你选择 OneDrive、Dropbox 或 Google Drive 文件夹时才会同步），设置和日志留在你的电脑上。智能保存和自动字幕只在你的电脑上收听，你的智能保存口令也只录在那里。
- 网站登录保存在 Kairos 自己的浏览器里，就在你的电脑上（Microsoft Edge WebView2，和 Edge 一样受 Windows 保护），从不写入 Kairos 的设置或日志。退出某个网站时，也会退出你登录它所用的 Google、Apple 或 Facebook 账号。
- **保存报告** 包含 Kairos 的日志和设置，但不含你的 Windows 用户名和窗口标题，也从不包含网站登录或片段。
- 发现了安全问题？请私下报告：见 [SECURITY.md](../SECURITY.md)。

## 常见问题

**Kairos 与反作弊兼容吗？**

Kairos 从不读取或修改游戏内存。为捕获全屏游戏（在所有模式下，拉伸或独占全屏的游戏也不会变黑），它使用与 OBS Studio 相同的、已签名且兼容反作弊的捕获钩子，Riot Vanguard、Easy Anti-Cheat 和 BattlEye 均允许。如果某个游戏通过钩子没有画面，Kairos 会自动改录屏幕，所以片段永远不会是黑的。

**录制时会出现黄色边框吗？**

Windows 11 上不会。在 Windows 10 上，**应用** 模式下 Windows 自己会在应用周围画出黄色边框，且不允许程序隐藏；**全屏** 和 **游戏** 模式从不显示。

**高于 60 帧时片段卡顿。**

片段不会比你的屏幕或游戏更流畅：在 60 Hz 屏幕上以 144 帧录制会不均匀地重复帧。Kairos 会在帧率下方提示，“画质”中的 **自动（推荐）** 也从不超过屏幕刷新率。60 帧适合 YouTube、X、TikTok 和 Instagram。

**出了问题，该发送什么？**

在“状态”页点击 **保存报告**：桌面上会出现一个包含 Kairos 日志和设置的 zip。请在我们的 [Discord](https://discord.gg/QgqsGEWzaF) 或 GitHub issue 中发送。

## 从源码构建

<details>
<summary>面向开发者</summary>

Kairos 是一个 .NET 10 应用（WPF 和 WinForms），内置 [OBS Studio](https://obsproject.com) 的引擎 **libobs**（只包含所需模块），并使用 FFmpeg 进行编辑和分享。

```powershell
.\tools\build-whisper.ps1               # once: the auto captions engine (needs the Vulkan SDK and VS Build Tools)
.\tools\release.ps1                     # the Setup, update files and source zip in releases\ (needs vpk 1.2.161)
dotnet run --project tests\Kairos.Tests # the tests
```

构建时会从已安装的 OBS Studio 复制 libobs 32.2.2。

</details>

## 许可证

Kairos 是自由软件，采用 [GNU 通用公共许可证 v2.0 或更高版本](../LICENSE)，与 libobs 的许可证相同。每个版本都附带完整源代码。第三方组件及其许可证列于 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。

<sub>*Kairos*（καιρός）在希腊语中意为“*恰当的时刻*”。</sub>
