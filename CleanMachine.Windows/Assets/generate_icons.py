"""Generate CleanMachine icon assets.

Design: "Fresh Screen"
  - Full-bleed rounded square, deep-green vertical gradient (app palette).
  - A light laptop with a bright teal screen (the "machine").
  - A warm amber brush sweeps across the screen - the warm handle keeps the
    mark distinct from the usual cool cleaning-broom look.
  - A translucent swipe on the screen and a small sparkle = freshly cleaned.

Everything is drawn in a 512x512 design space and rendered at 4x
supersampling, then Lanczos-downscaled, so small sizes stay crisp.

Outputs (same filenames the appxmanifest already references):
  StoreLogo.png (50), Square44x44Logo.png (44), Square150x150Logo.png (150),
  Square44x44Logo.targetsize-24_altform-unplated.png (24),
  Square44x44Logo.targetsize-256_altform-unplated.png (256),
  SplashScreen.scale-200.png (620x300), Wide310x150Logo.png (310x150),
  SmallTile.scale-200.png (120), LargeTile.scale-200.png (260),
  AppLogo.png (96, in-app sidebar mark),
  app.ico (256/48/32/24/16, PNG-compressed frames).

icon-master.svg mirrors this geometry as the human-readable source of truth.
"""

import io
import math
import struct
from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).parent
SS = 4  # supersample factor

# ---------------------------------------------------------------- design ---
# All coordinates in a 512x512 design space.
BG_TOP = (32, 78, 66)       # #204E42
BG_BOT = (15, 43, 36)       # #0F2B24
CORNER = 115                # rounded-square corner radius (22.5%)

DECK = (214, 228, 221)      # laptop base / deck
DECK_EDGE = (180, 201, 191)
BEZEL_TOP = (240, 247, 244)  # light device body
BEZEL_BOT = (205, 223, 215)
SCREEN_TOP = (95, 214, 176)  # bright teal screen
SCREEN_BOT = (44, 150, 121)

HANDLE0 = (243, 183, 78)    # amber handle (near ferrule)
HANDLE1 = (222, 132, 44)    # amber handle (far end)
FERRULE = (222, 230, 227)   # metal band
BRISTLE = (238, 249, 243)
BRISTLE_LINE = (183, 214, 200)
SPARK = (234, 251, 243)

# Laptop geometry (design space).
DECK_TOP = [(150, 300), (362, 300), (398, 346), (114, 346)]
DECK_LIP = [114, 338, 398, 354]          # x0, y0, x1, y1
BEZEL = [138, 116, 374, 304]
SCREEN = [156, 134, 356, 286]
SPARK_C = (190, 168)
SPARK_R = 26
SPARK_IN = 0.34
SWIPE = [(300, 196), (232, 230), (182, 238)]
SWIPE_W = 30
SWIPE_ALPHA = 0.28

# Brush: local frame with +x toward the handle, tip pivoting on the screen.
BRUSH_PIVOT = (238, 205)
BRUSH_ANGLE = -33            # degrees
BRISTLE_POLY = [(2, -54), (66, -42), (66, 42), (2, 54)]
FERRULE_AXIS = (74, 90)      # local x span of the ferrule band
FERRULE_W = 84
HANDLE_AXIS = (96, 238)      # local x span of the handle centreline
HANDLE_W = 60

# ------------------------------------------------------------- helpers ----


def lerp(c0, c1, t):
    return tuple(int(round(c0[i] + (c1[i] - c0[i]) * t)) for i in range(3))


def _vgrad(w, h, top, bottom):
    g = Image.new("RGB", (1, h))
    for y in range(h):
        g.putpixel((0, y), lerp(top, bottom, y / max(1, h - 1)))
    return g.resize((w, h))


def rounded_gradient_bg(size_px, radius, top, bottom):
    """Rounded square filled with a vertical gradient, transparent outside.
    `radius` is in 512-design units and scales with the target size."""
    big = size_px * SS
    grad = _vgrad(big, big, top, bottom)
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, big - 1, big - 1], radius=radius * big / 512.0, fill=255)
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    img.paste(grad, (0, 0), mask)
    return img


def rounded_rect_grad(canvas, box, radius, top, bottom):
    """Paste a vertically-graded rounded rectangle onto an RGBA canvas."""
    x0, y0, x1, y1 = [int(round(v)) for v in box]
    w, h = x1 - x0, y1 - y0
    if w <= 0 or h <= 0:
        return
    grad = _vgrad(w, h, top, bottom).convert("RGBA")
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, w - 1, h - 1], radius=radius, fill=255)
    canvas.paste(grad, (x0, y0), mask)


def dot_path(draw, pts, width, c0, c1):
    """Thick polyline as overlapping round dots, colour lerped along its
    length. Smooth round caps and per-position gradients."""
    segs, total = [], 0.0
    for a, b in zip(pts, pts[1:]):
        d = math.hypot(b[0] - a[0], b[1] - a[1]); segs.append((a, b, d)); total += d
    if total <= 0:
        return
    r = width / 2.0
    n = max(2, int(total / max(0.75, width * 0.2)))
    for i in range(n + 1):
        t = i / n; dist = t * total; acc = 0.0; px, py = pts[0]
        for a, b, d in segs:
            if acc + d >= dist or (a, b, d) is segs[-1]:
                f = 0.0 if d == 0 else (dist - acc) / d
                px = a[0] + (b[0] - a[0]) * f; py = a[1] + (b[1] - a[1]) * f; break
            acc += d
        col = lerp(c0, c1, t)
        draw.ellipse([px - r, py - r, px + r, py + r], fill=col)


def sparkle_points(cx, cy, r, inner):
    pts = []
    for k in range(8):
        ang = math.radians(k * 45 - 90)
        rad = r if k % 2 == 0 else r * inner
        pts.append((cx + rad * math.cos(ang), cy + rad * math.sin(ang)))
    return pts


def render_mark(px):
    """Render the full icon at px x px, supersampled internally."""
    S = px * SS / 512.0
    canvas = rounded_gradient_bg(px, CORNER, BG_TOP, BG_BOT)

    def s(v):
        return v * S

    # laptop base / deck (trapezoid) + front lip
    d = ImageDraw.Draw(canvas)
    d.polygon([(s(x), s(y)) for x, y in DECK_TOP], fill=DECK)
    d.rounded_rectangle([s(DECK_LIP[0]), s(DECK_LIP[1]), s(DECK_LIP[2]), s(DECK_LIP[3])],
                        radius=s(8), fill=DECK_EDGE)

    # screen bezel + inner screen
    rounded_rect_grad(canvas, [s(v) for v in BEZEL], s(22), BEZEL_TOP, BEZEL_BOT)
    rounded_rect_grad(canvas, [s(v) for v in SCREEN], s(12), SCREEN_TOP, SCREEN_BOT)

    # translucent clean-swipe on the screen (own layer, then faded)
    fx = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    dot_path(ImageDraw.Draw(fx), [(s(x), s(y)) for x, y in SWIPE], s(SWIPE_W),
             (255, 255, 255), (255, 255, 255))
    fx = fx.point(lambda a: int(a * SWIPE_ALPHA) if a else 0)
    canvas = Image.alpha_composite(canvas, fx)

    d = ImageDraw.Draw(canvas)
    d.polygon([(s(x), s(y)) for x, y in sparkle_points(*SPARK_C, SPARK_R, SPARK_IN)], fill=SPARK)

    # brush, placed by rotating a local axis frame about the tip pivot
    A = math.radians(BRUSH_ANGLE)
    cx, cy = s(BRUSH_PIVOT[0]), s(BRUSH_PIVOT[1])
    ux, uy = math.cos(A), math.sin(A)
    nx, ny = -math.sin(A), math.cos(A)

    def w(lx, ly):
        return (cx + lx * S * ux + ly * S * nx, cy + lx * S * uy + ly * S * ny)

    d.polygon([w(x, y) for x, y in BRISTLE_POLY], fill=BRISTLE)
    for ly in (-26, 0, 26):
        d.line([w(8, ly), w(64, ly * 0.78)], fill=BRISTLE_LINE, width=max(1, int(s(3))))
    dot_path(d, [w(FERRULE_AXIS[0], 0), w(FERRULE_AXIS[1], 0)], s(FERRULE_W), FERRULE, FERRULE)
    dot_path(d, [w(HANDLE_AXIS[0], 0), w(HANDLE_AXIS[1], 0)], s(HANDLE_W), HANDLE0, HANDLE1)

    return canvas.resize((px, px), Image.LANCZOS)


def render_centered(w, h, mark_px):
    """Transparent canvas w x h with the mark centered (splash, wide tiles)."""
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    img.alpha_composite(render_mark(mark_px), ((w - mark_px) // 2, (h - mark_px) // 2))
    return img


def write_ico(path, frames):
    """Multi-resolution ICO with PNG-compressed entries (all sizes <= 256)."""
    pngs = []
    for im in frames:
        b = io.BytesIO(); im.save(b, "PNG"); pngs.append(b.getvalue())
    n = len(frames)
    directory = struct.pack("<HHH", 0, 1, n)
    payload = b""
    offset = 6 + 16 * n
    for im, png in zip(frames, pngs):
        w, h = im.size
        entry = bytes([w & 0xFF, h & 0xFF, 0, 0]) + struct.pack("<HHII", 1, 32, len(png), offset)
        assert len(entry) == 16, "ICONDIRENTRY must be 16 bytes"
        directory += entry; payload += png; offset += len(png)
    path.write_bytes(directory + payload)


def main():
    jobs = [
        ("StoreLogo.png", 50),
        ("Square44x44Logo.png", 44),
        ("Square150x150Logo.png", 150),
        ("Square44x44Logo.targetsize-24_altform-unplated.png", 24),
        ("Square44x44Logo.targetsize-256_altform-unplated.png", 256),
        ("SmallTile.scale-200.png", 120),
        ("LargeTile.scale-200.png", 260),
        ("AppLogo.png", 96),
    ]
    for name, size in jobs:
        render_mark(size).save(OUT / name)
        print("wrote", name, size)

    for name, (w, h) in {
        "SplashScreen.scale-200.png": (620, 300),
        "Wide310x150Logo.png": (310, 150),
    }.items():
        render_centered(w, h, min(w, h)).save(OUT / name)
        print("wrote", name, (w, h))

    write_ico(OUT / "app.ico", [render_mark(s) for s in (256, 48, 32, 24, 16)])
    print("wrote app.ico (frames: 256/48/32/24/16)")


if __name__ == "__main__":
    main()
