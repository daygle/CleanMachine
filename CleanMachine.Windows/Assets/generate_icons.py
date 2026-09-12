"""Generate CleanMachine icon assets.

Design: "Clean Cycle"
  - Full-bleed rounded square, deep-green vertical gradient (app palette).
  - A bold ring (the cleaning cycle / background watch) with a gap in the
    upper-right; a bright checkmark runs through the gap, tail exiting past
    the ring = "cycle complete".
  - A 4-point sparkle above the gap = the fresh, clean result.

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
BG_TOP = (32, 78, 66)      # #204E42
BG_BOT = (15, 43, 36)      # #0F2B24
CORNER = 115               # rounded-square corner radius (22.5%)

RING_C = (248, 272)        # ring center (optically shifted left/down)
RING_R = 150               # ring radius (to stroke center)
RING_W = 56                # ring stroke width
ARC_START = 8              # degrees (0 = 3 o'clock, clockwise, y-down)
ARC_END = 282              # -> gap spans 282..368 (upper-right, centered 325)
RING_C0 = (63, 160, 128)   # ring gradient at ARC_START   #3FA080
RING_C1 = (159, 235, 206)  # ring gradient at ARC_END     #9FEBCE

CHK = [(168, 262), (232, 330), (404, 156)]  # start, valley, tip (exits gap)
CHK_W = 52
CHK_C0 = (79, 184, 148)    # check gradient at start     #4FB894
CHK_C1 = (180, 245, 220)   # check gradient at tip       #B4F5DC

SPARK_C = (438, 96)        # sparkle center
SPARK_R = 40               # sparkle outer radius
SPARK_IN = 0.22            # sparkle inner radius (fraction of outer)
SPARK_COL = (201, 247, 227)  # #C9F7E3

# ------------------------------------------------------------- helpers ----


def lerp(c0, c1, t):
    return tuple(int(round(c0[i] + (c1[i] - c0[i]) * t)) for i in range(3))


def dot_path(draw, pts, width, c0, c1):
    """Draw a thick polyline as overlapping round dots, color lerped along
    its arc length. Gives smooth round caps and per-position gradients."""
    # cumulative length
    segs = []
    total = 0.0
    for a, b in zip(pts, pts[1:]):
        d = math.hypot(b[0] - a[0], b[1] - a[1])
        segs.append((a, b, d))
        total += d
    if total <= 0:
        return
    step = max(0.75, width * 0.22)
    r = width / 2.0
    n = max(2, int(total / step))
    for i in range(n + 1):
        t = i / n
        dist = t * total
        # locate point at `dist`
        acc = 0.0
        px, py = pts[0]
        for a, b, d in segs:
            if acc + d >= dist or (a, b, d) is segs[-1]:
                f = 0.0 if d == 0 else (dist - acc) / d
                px = a[0] + (b[0] - a[0]) * f
                py = a[1] + (b[1] - a[1]) * f
                break
            acc += d
        col = lerp(c0, c1, t)
        draw.ellipse([px - r, py - r, px + r, py + r], fill=col)


def rounded_gradient_bg(size_px, radius, top, bottom):
    """Rounded square filled with a vertical gradient, transparent outside."""
    big = size_px * SS
    grad = Image.new("RGB", (1, big))
    for y in range(big):
        grad.putpixel((0, y), lerp(top, bottom, y / max(1, big - 1)))
    grad = grad.resize((big, big))
    mask = Image.new("L", (big, big), 0)
    md = ImageDraw.Draw(mask)
    md.rounded_rectangle([0, 0, big - 1, big - 1], radius=radius * SS, fill=255)
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    img.paste(grad, (0, 0), mask)
    return img


def sparkle_points(cx, cy, r, inner):
    pts = []
    for k in range(8):
        ang = math.radians(k * 45 - 90)  # start at top, clockwise
        rad = r if k % 2 == 0 else r * inner
        pts.append((cx + rad * math.cos(ang), cy + rad * math.sin(ang)))
    return pts


def render_mark(px):
    """Render the full icon (rounded square + ring + check + sparkle) at
    px x px, supersampled internally."""
    S = px * SS / 512.0
    canvas = rounded_gradient_bg(px, CORNER, BG_TOP, BG_BOT)
    ov = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(ov)

    # ring: sample the arc densely, gradient from ARC_START to ARC_END
    arc = []
    steps = 200
    for i in range(steps + 1):
        a = math.radians(ARC_START + (ARC_END - ARC_START) * i / steps)
        arc.append((RING_C[0] * S + RING_R * S * math.cos(a),
                    RING_C[1] * S + RING_R * S * math.sin(a)))
    dot_path(d, arc, RING_W * S, RING_C0, RING_C1)

    # check: short stroke -> valley -> long stroke exiting the gap
    dot_path(d, [(x * S, y * S) for x, y in CHK], CHK_W * S, CHK_C0, CHK_C1)

    # sparkle
    sp = [(x * S, y * S) for x, y in sparkle_points(*SPARK_C, SPARK_R, SPARK_IN)]
    d.polygon(sp, fill=SPARK_COL)

    out = Image.alpha_composite(canvas, ov)
    return out.resize((px, px), Image.LANCZOS)


def render_centered(w, h, mark_px):
    """Transparent canvas w x h with the mark centered (splash, wide tiles)."""
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    mark = render_mark(mark_px)
    img.alpha_composite(mark, ((w - mark_px) // 2, (h - mark_px) // 2))
    return img


def write_ico(path, frames):
    """Multi-resolution ICO with PNG-compressed entries (all sizes <= 256).
    ICONDIRENTRY (16 bytes): width, height, colors, reserved, planes (u16),
    bpp (u16), data size (u32), data offset (u32)."""
    pngs = []
    for im in frames:
        b = io.BytesIO()
        im.save(b, "PNG")
        pngs.append(b.getvalue())
    n = len(frames)
    directory = struct.pack("<HHH", 0, 1, n)
    payload = b""
    offset = 6 + 16 * n  # all entries live before any image data
    for im, png in zip(frames, pngs):
        w, h = im.size
        # width/height byte: 0 means 256; planes=1, bpp=32 per ICO convention
        entry = bytes([w & 0xFF, h & 0xFF, 0, 0]) + struct.pack("<HHII", 1, 32, len(png), offset)
        assert len(entry) == 16, "ICONDIRENTRY must be 16 bytes"
        directory += entry
        payload += png
        offset += len(png)
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
        # Dedicated in-app mark: rendered at 96px so the 30px sidebar logo stays
        # crisp up to 300% display scaling without borrowing a tile asset.
        ("AppLogo.png", 96),
    ]
    for name, size in jobs:
        img = render_mark(size)
        img.save(OUT / name)
        print("wrote", name, img.size)

    for name, (w, h) in {
        "SplashScreen.scale-200.png": (620, 300),
        "Wide310x150Logo.png": (310, 150),
    }.items():
        img = render_centered(w, h, min(w, h))
        img.save(OUT / name)
        print("wrote", name, img.size)

    frames = [render_mark(s) for s in (256, 48, 32, 24, 16)]
    write_ico(OUT / "app.ico", frames)
    print("wrote app.ico (frames: 256/48/32/24/16)")


if __name__ == "__main__":
    main()
