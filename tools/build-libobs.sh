#!/usr/bin/env bash
# Builds libobs 32.2.2 (the version src/Kairos/Native/Obs.cs follows) for Linux, without OBS Studio's Qt app, into a
# relocatable prefix: lib/libobs.so.30, lib/obs-plugins/, share/obs/ and bin/ (obs-ffmpeg-mux, obs-nvenc-test).
#
#   tools/build-libobs.sh [prefix]        default prefix: ~/obs-prefix (what Kairos.Linux looks for in development)
#
# Ubuntu 24.04 / Mint 22 build packages:
#   sudo apt install build-essential cmake ninja-build pkg-config extra-cmake-modules uthash-dev libcurl4-openssl-dev \
#     libx264-dev libssl-dev libmbedtls-dev libjansson-dev libffmpeg-nvenc-dev libgl1-mesa-dev libpci-dev libva-dev \
#     libdrm-dev libpipewire-0.3-dev libpulse-dev libasound2-dev libx11-xcb-dev libxcb-xinerama0-dev libxcb-composite0-dev \
#     libxcb-randr0-dev libxcb-xfixes0-dev libxcb-xinput-dev libxcb-shm0-dev libxcomposite-dev libxkbcommon-dev \
#     libwayland-dev libspeexdsp-dev libsimde-dev librist-dev libsrt-openssl-dev libqrcodegencpp-dev
set -euo pipefail
PREFIX="${1:-$HOME/obs-prefix}"
SRC="${OBS_SRC:-$HOME/build/obs-src}"
TAG=32.2.2

[ -d "$SRC" ] || git clone --depth 1 --branch "$TAG" https://github.com/obsproject/obs-studio "$SRC"

# The frontend-tools plugin needs Qt and has no switch of its own; the browser and websocket submodules are not fetched.
python3 - "$SRC/plugins/CMakeLists.txt" <<'PY'
import sys
p = sys.argv[1]
s = open(p).read()
s = s.replace("frontend-tools\n    PLATFORMS WINDOWS MACOS LINUX", "frontend-tools\n    PLATFORMS WINDOWS MACOS")
s = s.replace("\ncheck_obs_browser()\n", "\n").replace("\ncheck_obs_websocket()\n", "\n")
open(p, "w").write(s)
PY

# Kairos's own change to OBS: FFmpeg's fast AAC coder (about half the audio CPU of the default; see the patch's comment).
git -C "$SRC" apply --check "$(dirname "$0")/patches/obs-aac-fast-coder.patch" 2>/dev/null &&
  git -C "$SRC" apply "$(dirname "$0")/patches/obs-aac-fast-coder.patch"

# Header-only dependencies that are not worth a system package: use a local copy when the system has none.
EXTRA=()
[ -e /usr/share/ECM/cmake/ECMConfig.cmake ] || EXTRA+=("-DECM_DIR=${ECM_DIR:?install extra-cmake-modules or set ECM_DIR}")
[ -e /usr/include/uthash.h ] || EXTRA+=("-DUthash_INCLUDE_DIR=${UTHASH_INCLUDE_DIR:?install uthash-dev or set UTHASH_INCLUDE_DIR}")

cmake -S "$SRC" -B "$SRC/build" -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$PREFIX" \
  -DENABLE_RELOCATABLE=ON "${EXTRA[@]}" \
  -DENABLE_UI=OFF -DENABLE_FRONTEND=OFF -DENABLE_BROWSER=OFF -DENABLE_WEBSOCKET=OFF -DENABLE_SCRIPTING=OFF \
  -DENABLE_VLC=OFF -DENABLE_WEBRTC=OFF -DENABLE_NEW_MPEGTS_OUTPUT=OFF -DENABLE_AJA=OFF -DENABLE_DECKLINK=OFF \
  -DENABLE_VST=OFF -DENABLE_LIBFDK=OFF -DENABLE_QSV11=OFF -DENABLE_V4L2=OFF -DENABLE_JACK=OFF -DENABLE_SNDIO=OFF \
  -DENABLE_NVENC=ON -DENABLE_WAYLAND=ON -DENABLE_PIPEWIRE=ON -DENABLE_PULSEAUDIO=ON -DENABLE_ALSA=ON -DENABLE_HEVC=ON \
  -DBUILD_TESTS=OFF
cmake --build "$SRC/build" -j"$(nproc)"
cmake --install "$SRC/build"
echo "libobs $TAG installed in $PREFIX"
