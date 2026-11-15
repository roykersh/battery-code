"""Builds assets/showcase.gif from the frames that `batteryfloat.exe --demo <dir>` renders.

Run: py -3.13 make_gif.py <frames dir>
The demo uses made-up devices (it never reads the real ones), drawn by the widget's own code.
"""
import os
import sys

from PIL import Image

frames_dir = sys.argv[1]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "assets", "showcase.gif")
os.makedirs(os.path.dirname(out), exist_ok=True)

images, durations = [], []
with open(os.path.join(frames_dir, "frames.txt"), encoding="utf-8") as f:
    for line in f:
        name, ms = line.split()
        img = Image.open(os.path.join(frames_dir, name)).convert("RGB")
        images.append(img.quantize(colors=255, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE))
        durations.append(int(ms))

images[0].save(out, save_all=True, append_images=images[1:], duration=durations, loop=0, optimize=True, disposal=1)
print(out, len(images), "frames", os.path.getsize(out) // 1024, "KB")
