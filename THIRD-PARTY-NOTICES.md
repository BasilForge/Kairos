# Third-party notices

Kairos's own source code is licensed under the GNU General Public License, version 2 or (at your option) any later version. See [LICENSE](LICENSE).

Release builds include the following components, copied unmodified from OBS Studio 32.2.2 (<https://github.com/obsproject/obs-studio>).

| Component | Files | License | Source |
|---|---|---|---|
| libobs and its graphics backends | `obs.dll`, `libobs-d3d11.dll`, `libobs-winrt.dll`, `w32-pthreads.dll`, `data/libobs` | GPL-2.0-or-later | obs-studio repository, tag 32.2.2 |
| OBS plugins | `win-capture`, `win-wasapi`, `obs-nvenc`, `obs-ffmpeg`, `obs-qsv11`, `obs-x264`, `win-dshow` (cameras; OBS's virtual camera files are left out), `obs-filters` (the round camera's mask) | GPL-2.0-or-later | obs-studio repository, tag 32.2.2 |
| OBS helper programs | `obs-ffmpeg-mux.exe`, `obs-nvenc-test.exe`, `obs-amf-test.exe`, `obs-qsv-test.exe` | GPL-2.0-or-later | obs-studio repository, tag 32.2.2 |
| FFmpeg libraries | `avcodec`, `avformat`, `avutil`, `avfilter`, `avdevice`, `swscale`, `swresample` | LGPL-2.1-or-later, GPL in this build | <https://ffmpeg.org> (OBS dependency build) |
| x264 | `libx264-*.dll` | GPL-2.0-or-later | <https://www.videolan.org/developers/x264.html> |
| zlib | `zlib.dll` | zlib License | <https://zlib.net> |
| libcurl | `libcurl.dll` | curl License (MIT-style) | <https://curl.se> |
| SRT, librist | `srt.dll`, `librist.dll` | MPL-2.0, BSD-2-Clause | OBS dependency build |
| NVIDIA Video Codec SDK headers | compiled into `obs-nvenc` | MIT | NVIDIA |
| AMD AMF headers | compiled into `obs-ffmpeg` | MIT | AMD GPUOpen |
| Intel oneVPL / VPL | compiled into `obs-qsv11` | MIT | Intel |

Smart Save (voice-triggered saving) uses these components, unmodified:

| Component | Files | License | Source |
|---|---|---|---|
| ONNX Runtime 1.30.0 | `onnxruntime.dll`, `onnxruntime_providers_shared.dll`, `Microsoft.ML.OnnxRuntime.dll` | MIT | <https://github.com/microsoft/onnxruntime> |
| openWakeWord feature models | `models/melspectrogram.onnx`, `models/embedding_model.onnx` | Apache-2.0; the embedding model is a re-implementation of Google's `speech_embedding` TF Hub module (Apache-2.0) | <https://github.com/dscripka/openWakeWord>, release v0.5.1 |
| Silero VAD | `models/silero_vad.onnx` | MIT | <https://github.com/snakers4/silero-vad> (as packaged in openWakeWord v0.5.1) |

Their license texts ship in the `licenses` folder: `ONNX-Runtime-LICENSE.txt` and `ONNX-Runtime-ThirdPartyNotices.txt` (Copyright (c) Microsoft Corporation), `Apache-2.0.txt` (openWakeWord and Google speech_embedding) and `Silero-VAD-LICENSE.txt` (Copyright (c) 2020-present Silero Team).

Sharing (converting a clip for X, TikTok, Instagram or the clipboard) uses the FFmpeg libraries listed above through:

| Component | Files | License | Source |
|---|---|---|---|
| FFmpeg.AutoGen 8.1.0 | `FFmpeg.AutoGen.dll` | MIT | <https://github.com/Ruslan-B/FFmpeg.AutoGen> |
| Microsoft WebView2 SDK 1.0.4258.31 | `Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.Wpf.dll`, `Microsoft.Web.WebView2.WinForms.dll`, `WebView2Loader.dll` | BSD-3-Clause (Microsoft) | <https://www.nuget.org/packages/Microsoft.Web.WebView2> |

Their license texts ship as `licenses/FFmpeg.AutoGen-LICENSE.txt` (Copyright (c) 2025 Ruslan Balanukhin), `licenses/WebView2-SDK-LICENSE.txt` and `licenses/WebView2-SDK-NOTICE.txt` (Copyright (C) Microsoft Corporation). X, TikTok and Instagram open in Kairos's own window on the Edge WebView2 runtime that comes with Windows (not redistributed). Kairos posts the prepared file and caption through each site's own page when the user presses "Post to …" in Kairos; it uses no service API, and sign-ins stay on the PC (`%LOCALAPPDATA%\Kairos\WebView`) until the user signs out in the share panel.

Auto captions in the editor use:

| Component | Files | License | Source |
|---|---|---|---|
| whisper.cpp 1.9.5 and ggml, built with the Vulkan backend | `whisper.dll`, `ggml.dll`, `ggml-base.dll`, `ggml-cpu.dll`, `ggml-vulkan.dll`: not in the installer; downloaded on first use with the model, after the user agrees, into `%LOCALAPPDATA%\Kairos\captions\runtime` | MIT | <https://github.com/ggml-org/whisper.cpp>, tag v1.9.5 (built unmodified by `tools/build-whisper.ps1`) |
| Whisper large-v3-turbo model, quantized (`ggml-large-v3-turbo-q5_0.bin`) | not shipped: downloaded on first use, after the user agrees, into `%LOCALAPPDATA%\Kairos\captions\model` | MIT (OpenAI Whisper) | <https://huggingface.co/ggerganov/whisper.cpp> |

Its license text ships as `licenses/whisper.cpp-LICENSE.txt` (Copyright (c) 2023-2026 The ggml authors).

The editor's text tool ships these fonts, unmodified, from Google Fonts (<https://github.com/google/fonts>, `ofl` folder), in `fonts`:

| Font | File | License | Copyright |
|---|---|---|---|
| Almarai | `Almarai-ExtraBold.ttf` | SIL Open Font License 1.1 | Copyright 2019 The Almarai Project Authors |
| Lalezar | `Lalezar-Regular.ttf` | SIL Open Font License 1.1 | Copyright 2015 The Lalezar Project Authors |
| Rakkas | `Rakkas-Regular.ttf` | SIL Open Font License 1.1 | Copyright 2016 Zeynep Akay |
| Aref Ruqaa | `ArefRuqaa-Bold.ttf` | SIL Open Font License 1.1 | Copyright 2015-2020 The Aref Ruqaa Project Authors |
| Cairo | `Cairo-Bold.ttf` | SIL Open Font License 1.1 | Copyright 2009 The Cairo Project Authors |
| Tajawal | `Tajawal-Bold.ttf` | SIL Open Font License 1.1 | Copyright 2018 Boutros International |
| Changa | `Changa-SemiBold.ttf` | SIL Open Font License 1.1 | Copyright 2018 The Changa Project Authors |
| Reem Kufi | `ReemKufi-Regular.ttf` | SIL Open Font License 1.1 | Copyright 2015-2022 The Reem Kufi Project Authors |
| Lemonada | `Lemonada-SemiBold.ttf` | SIL Open Font License 1.1 | Copyright 2011 The Lemonada Project Authors |

Their license texts ship as `licenses/Almarai-OFL.txt`, `licenses/Lalezar-OFL.txt`, `licenses/Rakkas-OFL.txt`, `licenses/ArefRuqaa-OFL.txt`,
`licenses/Cairo-OFL.txt`, `licenses/Tajawal-OFL.txt`, `licenses/Changa-OFL.txt`, `licenses/ReemKufi-OFL.txt` and `licenses/Lemonada-OFL.txt`.

Kairos's own complete source code for each release is published beside it as `Kairos-<version>-source.zip` (made from the exact commit the release was built from). The corresponding source code for every other GPL component is available at the links above, at the version tag listed. The .NET runtime (MIT) is included in the program.

Installing and updating use **Velopack 1.2.161** (`Velopack.dll` in the program, and Velopack's `Update.exe` and the
Kairos.exe stub in the install folder), MIT License, Copyright (c) 2021 Caelan Sayler and (c) 2024 Velopack Ltd.,
<https://github.com/velopack/velopack>. Its license text ships as `licenses/Velopack-LICENSE.txt`.

The icons in the interface come from the Segoe Fluent Icons font that ships with Windows; they are not redistributed.

The X, TikTok and Instagram marks in the share panel are trademarks of their owners and only identify where a clip is posted. The round logo images were supplied by the project owner (TikTok's note filled in white and Instagram's circle trimmed to the image edge, for the dark tiles).

The emoji pictures in the caption picker (`src/Kairos/Ui/Assets/Emoji`) are Microsoft's Fluent Emoji designs as Windows 11 draws them (Segoe UI Emoji), MIT License, Copyright (c) Microsoft Corporation, <https://github.com/microsoft/fluentui-emoji>.

## Wordmark fonts (in the name images only)

The name in the sidebar (`src/Kairos/Ui/Assets/wordmark-en.png`, `wordmark-ar.png`) is drawn from
**Fraunces** (Undercase Type) and **Aref Ruqaa** (Abdullah Aref), both under the SIL Open Font License 1.1.
The fonts themselves are not shipped.
