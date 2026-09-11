"""Generate CleanMachine icon assets.

Renders a "sweep-check" mark (rounded square + check with sweep tail)
at the sizes Windows/Appx need, writes PNGs and a ico file.

Sizes produced (padding-aware so the mark sits inside the safe area):
  - StoreLogo.png          50x50   (Appx store logo)
  - Square44x44Logo.png   44x44   (AppList / taskbar)
  - Square44x44Logo.png   24x24   (targetsize-24 tile, required by manifest's altform)
  - Square44x44Logo.png   256x256 (targetsize-256 altform)
  - Square150x150Logo.png 150x150 (start/misc)
  - SplashScreen.png       620x300 (wide splash, 150x150 safe area centered)
  - Wide310x150Logo.png    310x150 (wide tile)
  - SmallTile.scale-200.png 120x120 (small tile, 2x)
  - LargeTile.scale-200.png  260x260 (large tile, 2x)
  - app.ico                256/48/32/16 (exe + installer icon)
"""

from PIL import Image, ImageDraw, ImageFilter
from pathlib import Path
import math

OUT = Path(__file__).parent
GREEN_DEEP = (23, 60, 53, 255)        # #173C35
GREEN_MID  = (66, 115, 98, 255)       # #427362
GREEN_MINT = (143, 214, 192, 255)     # #8fd6c0
GREEN_LIGHT= (190, 229, 213, 255)     # #bfe5d5
WHITE      = (255, 255, 255, 255)

# Brand mark geometry, defined in a 256x256 coordinate space.
# Rounded square: rounded rect inset a bit from the border.
SQUARE_RADIUS = 64          # outer corner radius
MARK_STROKE   = 30          # stroke width of the sweep/check in 256-space
MARK_COLOR    = GREEN_MINT

def _aa_line(draw, p0, p1, width, color):
    # anti-aliased thick line via supersampled small image pasted with alpha mask
    import numpy as np
    dx = p1[0]-p0[0]; dy = p1[1]-p0[1]
    length = math.hypot(dx, dy) or 1.0
    nx = -dy/length; ny = dx/length
    half = width/2.0
    # bounding box
    xs = [p0[0], p1[0]]; ys=[p0[1], p1[1]]
    for s in (-1,1):
        for t in (-1,1):
            xs.append(p0[0]+s*half*nx + t*dx*0.0)
            ys.append(p0[1]+s*half*ny + t*dy*0.0)
    left=min(xs); right=max(xs); top=min(ys); bottom=max(ys)
    w = int(math.ceil(right-left+width*2)); h=int(math.ceil(bottom-top+width*2))
    if w<2 or h<2:
        return
    ox = left-width; oy=top-width
    buf = Image.new("L", (w,h), 0)
    bdraw = ImageDraw.Draw(buf)
    # sample many points along the segment and draw circles
    steps = max(2, int(length*1.5))
    for i in range(steps+1):
        t=i/steps
        cx = p0[0]+dx*t; cy=p0[1]+dy*t
        px = int(cx-ox); py=int(cy-oy)
        r=int(math.ceil(width/2))+1
        bdraw.ellipse([px-r,py-r,px+r,py+r], fill=255)
    # shrink to smooth edges a touch (anti-alias)
    buf = buf.filter(ImageFilter.GaussianBlur(max(0.6, width*0.18)))
    base = Image.new("L", (w,h), 0)
    base.paste(buf, (0,0))
    # trim
    alpha = np.array(base)/255.0
    canvas = Image.new("RGBA", (w,h), (0,0,0,0))
    canvas.putalpha(Image.fromarray((alpha*255).astype("uint8")))
    r,g,b = color[0],color[1],color[2]
    flat = Image.new("RGBA", (w,h), (r,g,b,255))
    flat.putalpha(Image.fromarray((alpha*255).astype("uint8")))
    return flat, ox, oy

def draw_mark(base_w, pad):
    """Draw the sweep-check mark into a base_w x base_w RGBA image.
    pad = outer clear margin (so the mark sits inside the safe area)."""
    W = base_w
    # scale all 256-space coords into the mark zone: zone = W - 2*pad
    zone = W - 2*pad
    scale = zone/256.0
    def P(x,y):
        return (pad + x*scale, pad + y*scale)
    img = Image.new("RGBA", (W,W), (0,0,0,0))
    # 1) deep green rounded square (full-bleed, corners rounded)
    radius = int(SQUARE_RADIUS*scale)
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0,0,W-1,W-1], radius=radius, fill=GREEN_DEEP)
    # subtle inner highlight band at top
    hl = Image.new("RGBA",(W,W),(0,0,0,0))
    hld = ImageDraw.Draw(hl)
    hld.rounded_rectangle([0,0,W-1,int(W*0.18)], radius=radius, fill=(255,255,255,22))
    img = Image.alpha_composite(img, hl)
    d = ImageDraw.Draw(img)
    # 2) sweep-check mark: two strokes (sweep tail + check), plus origin dot
    # sweep tail: from lower-left up to mid
    p0 = P(-62, 60); p1 = P(-40, 34); p2 = P(-18, 8); p3 = P(2, -10)
    segs = [(p0,p1),(p1,p2),(p2,p3)]
    for a,b in segs:
        res = _aa_line(d, a, b, MARK_STROKE*scale, MARK_COLOR)
        if res:
            frag, ox, oy = res
            img.alpha_composite(frag, (int(ox), int(oy)))
    # check upper stroke: from mid up-left to top, then down-right to lower-right
    pa = P(6, -10); pb = P(34, -46); pc = P(72, 34)
    for a,b in [(pa,pb),(pb,pc)]:
        res = _aa_line(d, a, b, MARK_STROKE*scale, MARK_COLOR)
        if res:
            frag, ox, oy = res
            img.alpha_composite(frag, (int(ox), int(oy)))
    # origin dot
    dx,dy = P(-64,62)
    r=int(9*scale)
    dd=ImageDraw.Draw(img)
    dd.ellipse([dx-r,dy-r,dx+r,dy+r], fill=GREEN_LIGHT)
    # soft drop shadow under mark for depth (optional subtle)
    return img

def write_png(img, name):
    path = OUT / name
    img.save(path)
    print("wrote", path, img.size)

def png_at(name, size, pad_frac=0.10):
    img = draw_mark(size, int(size*pad_frac))
    write_png(img, name)

def main():
    # Core Appx sizes
    png_at("StoreLogo.png", 50, pad_frac=0.10)
    png_at("Square44x44Logo.png", 44, pad_frac=0.06)
    png_at("Square150x150Logo.png", 150, pad_frac=0.08)
    # tile altforms referenced by manifest extras
    png_at("Square44x44Logo.targetsize-24_altform-unplated.png", 24, pad_frac=0.05)
    png_at("Square44x44Logo.targetsize-256_altform-unplated.png", 256, pad_frac=0.09)
    # splash / wide tiles (landscapes): draw mark centered, keep safe area
    def landscape_out(name, w, h, pad_frac=0.10):
        img = Image.new("RGBA",(w,h),(0,0,0,0))
        mark = draw_mark(min(w,h), int(min(w,h)*pad_frac))
        ox = (w-mark.width)//2; oy=(h-mark.height)//2
        img.alpha_composite(mark, (ox,oy))
        write_png(img, name)
    landscape_out("SplashScreen.scale-200.png", 620, 300, pad_frac=0.10)  # actual: 1240x600 if 2x but we keep 620x300 base name
    landscape_out("Wide310x150Logo.png", 310, 150, pad_frac=0.10)
    landscape_out("SmallTile.scale-200.png", 120, 120, pad_frac=0.10)
    landscape_out("LargeTile.scale-200.png", 260, 260, pad_frac=0.10)
    # exe + installer ico (multi-size)
    sizes = [256, 48, 32, 24, 16]
    # Build the .ico: Pillow's ICO writer keeps multiple frames when passed
    # append_images with distinct sizes (no `sizes` arg).
    srcs = [draw_mark(s, int(s*0.10)) for s in sizes]
    srcs[0].save(OUT/"app.ico", format="ICO", append_images=srcs[1:])
    print("wrote", OUT/"app.ico")
    # Square44x44Logo for installer (used in iss) already exists as png


if __name__ == "__main__":
    main()
