#!/usr/bin/env bash
# Fetches the FFmpeg 8.1 shared libraries (BtbN's GPL build, the version FFmpeg.AutoGen 8.1 binds to) for the editor, share and
# thumbnail code. Mint/Ubuntu ship FFmpeg 6, which that code cannot load.
#
#   tools/get-ffmpeg.sh [prefix]        default prefix: ~/ffmpeg8 (what Kairos.Linux looks for in development)
set -euo pipefail
PREFIX="${1:-$HOME/ffmpeg8}"
NAME=ffmpeg-n8.1-latest-linux64-gpl-shared-8.1
mkdir -p "$PREFIX"
curl -fSL "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$NAME.tar.xz" | tar -xJ -C "$PREFIX" --strip-components=1
echo "FFmpeg installed in $PREFIX ($(ls "$PREFIX/lib" | grep -c '\.so\.') libraries)"
