#!/usr/bin/env python3
"""Extracts the PNG frames of src/Kairos/app.ico into src/Kairos.Linux/Assets/icons/kairos-<size>.png (what a Linux icon theme wants)."""
import os, struct, sys
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
data = open(os.path.join(root, "src/Kairos/app.ico"), "rb").read()
out = os.path.join(root, "src/Kairos.Linux/Assets/icons")
os.makedirs(out, exist_ok=True)
for i in range(struct.unpack("<H", data[4:6])[0]):
    w, h, _, _, _, _, size, offset = struct.unpack("<BBBBHHII", data[6 + 16 * i:22 + 16 * i])
    side = w or 256
    open(os.path.join(out, f"kairos-{side}.png"), "wb").write(data[offset:offset + size])
    print("kairos-%d.png" % side, file=sys.stderr)
