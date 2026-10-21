#!/usr/bin/env bash
# Builds the auto captions engine (whisper.cpp 1.9.5, CPU; Vulkan too when glslc is installed) and installs the four
# libraries Kairos loads into the captions runtime folder:  tools/build-whisper.sh [dest]
# Needs: git, cmake, a C++ compiler.  If whisper.cpp is ever updated, measure WhisperNative's struct sizes again.
set -euo pipefail
tag=v1.9.5
dest=${1:-${XDG_DATA_HOME:-$HOME/.local/share}/Kairos/captions/runtime}
work=${TMPDIR:-/tmp}/kairos-whisper-build
[ -d "$work/src" ] || git clone --depth 1 --branch "$tag" https://github.com/ggml-org/whisper.cpp "$work/src"
vulkan=OFF; command -v glslc >/dev/null && [ -e /usr/include/vulkan/vulkan.h ] && vulkan=ON
cmake -S "$work/src" -B "$work/build" -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON -DGGML_NATIVE=OFF -DGGML_VULKAN=$vulkan \
  -DWHISPER_BUILD_TESTS=OFF -DWHISPER_BUILD_EXAMPLES=OFF -DCMAKE_BUILD_WITH_INSTALL_RPATH=ON -DCMAKE_INSTALL_RPATH='$ORIGIN'
cmake --build "$work/build" -j"$(nproc)"
mkdir -p "$dest"
for lib in libwhisper libggml libggml-base libggml-cpu $([ $vulkan = ON ] && echo libggml-vulkan); do
  cp -L "$work/build"/*/"$lib".so* "$work/build"/"$lib".so* "$dest/" 2>/dev/null || find "$work/build" -name "$lib.so*" -exec cp -L {} "$dest/" \;
done
echo "Installed to $dest (vulkan=$vulkan)"; ls -la "$dest"
