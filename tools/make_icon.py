# Draws the Battery Code icon in the same style as color-code / rtl-code:
# dark rounded tile, thick rounded neon strokes with a soft glow.
import sys
from PIL import Image, ImageDraw, ImageFilter, ImageChops
S = 4; N = 1024 * S
def lerp(a, b, t): return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))
def grad(size, stops, horizontal=True, diag=False):
    w, h = size; im = Image.new("RGB", size); px = im.load()
    for x in range(w):
        for y in range(0, h, 1 if diag else h):
            t = (x / w * 0.75 + y / h * 0.25) if diag else x / w
            for i in range(len(stops) - 1):
                t0, c0 = stops[i]; t1, c1 = stops[i + 1]
                if t0 <= t <= t1: c = lerp(c0, c1, (t - t0) / (t1 - t0)); break
            else: c = stops[-1][1]
            if diag: px[x, y] = c
            else:
                for yy in range(h): px[x, yy] = c
    return im
# background tile
tile = Image.new("L", (N, N), 0)
ImageDraw.Draw(tile).rounded_rectangle([0, 0, N - 1, N - 1], radius=230 * S, fill=255)
bg = Image.new("RGB", (N, N))
d = ImageDraw.Draw(bg)
for y in range(N):
    d.line([(0, y), (N, y)], fill=lerp((27, 30, 37), (13, 14, 18), y / N))
border = Image.new("L", (N, N), 0)
ImageDraw.Draw(border).rounded_rectangle([0, 0, N - 1, N - 1], radius=230 * S, outline=255, width=6 * S)
bg.paste((42, 45, 54), mask=border)
out = Image.new("RGBA", (N, N), (0, 0, 0, 0)); out.paste(bg, mask=tile)
# shapes (in 1024 units)
W = 72  # stroke width, same weight as the other icons
body = [200, 330, 760, 694]
mask_body = Image.new("L", (N, N), 0)
db = ImageDraw.Draw(mask_body)
db.rounded_rectangle([c * S for c in body], radius=110 * S, outline=255, width=W * S)
db.rounded_rectangle([790 * S, 440 * S, 850 * S, 584 * S], radius=30 * S, fill=255)  # terminal nub
mask_fill = Image.new("L", (N, N), 0)
ImageDraw.Draw(mask_fill).rounded_rectangle([300 * S, 460 * S, 560 * S, 564 * S], radius=52 * S, fill=255)
col_body = grad((N, N), [(0, (34, 211, 238)), (0.55, (59, 130, 246)), (1, (139, 92, 246))], diag=True)
col_fill = grad((N, N), [(0.29, (250, 204, 21)), (0.42, (163, 230, 53)), (0.55, (34, 197, 94))])
def glow(mask, col, radius, strength):
    g = mask.filter(ImageFilter.GaussianBlur(radius * S))
    g = g.point(lambda v: int(v * strength))
    layer = Image.new("RGBA", (N, N)); layer.paste(col, mask=g); return layer
for mask, col in ((mask_body, col_body), (mask_fill, col_fill)):
    out = Image.alpha_composite(out, glow(mask, col, 24, 0.95))
for mask, col in ((mask_body, col_body), (mask_fill, col_fill)):
    layer = Image.new("RGBA", (N, N)); layer.paste(col, mask=mask)
    out = Image.alpha_composite(out, layer)
a = ImageChops.multiply(out.getchannel("A"), tile); out.putalpha(a)
out.resize((1024, 1024), Image.LANCZOS).save(sys.argv[1])
