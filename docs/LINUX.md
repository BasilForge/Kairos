# Kairos on Linux

A native port (Avalonia UI, X11) in `src/Kairos.Linux`. The engine, editor logic, Smart Save and view models are shared with the Windows app. Build steps are in the README.

## Working
Replay buffer and saving (hotkeys, tray, `kairos --send save|screenshot|toggle|record|status`), Library, built-in player and picture viewer, editor (cut, speed, text, draw, shapes, blur, filters, crop, save as copy or replace), Smart Save (voice trigger), auto captions (Whisper), Share/Accounts (X, TikTok, Instagram through a Chromium-family browser), settings, autostart, launcher and icons.

## What is missing or weaker than on Windows
- **X11 only.** No Wayland: global hotkeys, window capture and the tray need X11.
- **Capture:** Games mode follows the focused window (xcomposite); true fullscreen games that the compositor unredirects are untested. Screen mode (xshm) costs much more CPU and can hurt game frame pacing. No PipeWire/portal screen capture yet.
- **Encoders:** NVENC and x264 only. No VAAPI/AMF/QSV in the capture engine.
- **Captions:** CPU-only unless Vulkan is built (needs `glslc`), so they are slow (about 5x real time here). The engine is built locally with `tools/build-whisper.sh`; there is no download or release asset.
- **Share:** needs Brave, Chrome, Chromium or Edge. The site recipes are the Windows ones and were checked only on local mock pages, not against the live sites or a real login. Without a browser, Share opens the site and puts the file on the clipboard.
- **Not tested:** tray icon and toast on Cinnamon (and other desktops), Draw/Shapes/Blur/Crop and "replace the original" in the editor, real sound in the player, Smart Save enrollment on other mics.
- **Packaging:** no AppImage, .deb or Flatpak; no auto-update. libobs and FFmpeg are built by scripts.
- **Tests:** the Linux suite runs the portable tests (15 skipped, each with its reason). The Windows UI tests (WPF rendering) have no Avalonia equivalent yet.
- **Small bugs:** the first click on an editor swatch right after typing text is swallowed; the editor and share windows are not RTL-checked.

## Worth improving
1. Wayland: PipeWire screen capture via the portal, and portal global shortcuts.
2. Packaging (AppImage/Flatpak) with libobs and FFmpeg bundled, plus a release asset for the captions engine.
3. Vulkan captions, or a smaller model option for CPU.
4. VAAPI encoding for AMD/Intel GPUs.
5. Headless Avalonia UI tests and CI for the Linux build.
6. Check the Share recipes against the live sites.
