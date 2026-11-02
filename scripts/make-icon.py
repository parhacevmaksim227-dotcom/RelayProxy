from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter, ImageChops
import math

root = Path(__file__).resolve().parent.parent
S = 1024  # master canvas; everything is drawn once and downsampled


def squircle_mask(size, inset, n=5.0):
    # iOS-style continuous corner: superellipse |x|^n + |y|^n = 1
    big = size * 2
    m = Image.new('L', (big, big), 0)
    px = m.load()
    c = big / 2
    r = (big - inset * 4) / 2
    for y in range(big):
        dy = abs(y - c) / r
        if dy > 1:
            continue
        span = (1 - dy ** n) ** (1 / n)
        x0 = int(c - span * r)
        x1 = int(c + span * r)
        for x in range(x0, x1 + 1):
            px[x, y] = 255
    return m.resize((size, size), Image.Resampling.LANCZOS)


def lerp(a, b, t):
    return a + (b - a) * t


def gradient(size, stops, angle_deg):
    # stops: [(t, (r, g, b))]; linear gradient across the whole square
    img = Image.new('RGB', (size, size))
    px = img.load()
    a = math.radians(angle_deg)
    dx, dy = math.cos(a), math.sin(a)
    lo = -(abs(dx) + abs(dy)) / 2
    for y in range(size):
        for x in range(size):
            u = ((x / size - .5) * dx + (y / size - .5) * dy - lo) / (abs(dx) + abs(dy))
            u = min(1, max(0, u))
            for i in range(len(stops) - 1):
                t0, c0 = stops[i]
                t1, c1 = stops[i + 1]
                if u <= t1:
                    k = (u - t0) / (t1 - t0) if t1 > t0 else 0
                    px[x, y] = tuple(int(lerp(c0[j], c1[j], k)) for j in range(3))
                    break
    return img


def blob(size, center, radius, color, alpha, blur):
    layer = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    cx, cy = center
    d.ellipse((cx - radius, cy - radius, cx + radius, cy + radius), fill=color + (alpha,))
    return layer.filter(ImageFilter.GaussianBlur(blur))


def bezier(p0, p1, p2, p3, steps=240):
    pts = []
    for i in range(steps + 1):
        t = i / steps
        u = 1 - t
        pts.append((u ** 3 * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t ** 3 * p3[0],
                    u ** 3 * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t ** 3 * p3[1]))
    return pts


def stroke(draw, pts, width, fill):
    r = width / 2
    for x, y in pts:
        draw.ellipse((x - r, y - r, x + r, y + r), fill=fill)


mask = squircle_mask(S, 60)

# Deep ocean base with aurora light pools.
base = gradient(S, [(0, (14, 22, 64)), (.55, (22, 74, 120)), (1, (24, 168, 160))], 62).convert('RGBA')
base = Image.alpha_composite(base, blob(S, (250, 220), 420, (120, 140, 255), 150, 120))
base = Image.alpha_composite(base, blob(S, (800, 860), 430, (80, 255, 210), 140, 130))
base = Image.alpha_composite(base, blob(S, (820, 180), 260, (190, 120, 255), 90, 110))

# Relay glyph: a bold "R" cut from the same glass as the tile.
from PIL import ImageFont
font = ImageFont.truetype('C:/Windows/Fonts/segoeuib.ttf', 640)
glyph = Image.new('RGBA', (S, S), (0, 0, 0, 0))
gd = ImageDraw.Draw(glyph)
box = gd.textbbox((0, 0), 'R', font=font)
gx = (S - (box[2] - box[0])) / 2 - box[0] + 10
gy = (S - (box[3] - box[1])) / 2 - box[1] - 6
gd.text((gx, gy), 'R', font=font, fill=(255, 255, 255, 255))
glyph_alpha = glyph.getchannel('A')

# Soft shadow under the glyph for depth.
shadow = Image.new('RGBA', (S, S), (4, 14, 40, 0))
shadow.putalpha(glyph_alpha.filter(ImageFilter.GaussianBlur(26)).point(lambda v: int(v * .55)))
shadow = ImageChops.offset(shadow, 0, 26)
base = Image.alpha_composite(base, shadow)

# Glass body for the glyph: bright top, translucent bottom, like frosted glass.
body = gradient(S, [(0, (255, 255, 255)), (.55, (210, 245, 255)), (1, (150, 232, 232))], 90).convert('RGBA')
body.putalpha(glyph_alpha.point(lambda v: int(v * .92)))
base = Image.alpha_composite(base, body)

# Inner rim: lighter edge on the glyph's own silhouette.
edge = ImageChops.subtract(glyph_alpha, glyph_alpha.filter(ImageFilter.MinFilter(9)))
rim = Image.new('RGBA', (S, S), (255, 255, 255, 0))
rim.putalpha(edge.filter(ImageFilter.GaussianBlur(1.5)))
base = Image.alpha_composite(base, rim)

# Lens highlight over the whole tile.
shine = Image.new('RGBA', (S, S), (0, 0, 0, 0))
sd = ImageDraw.Draw(shine)
sd.ellipse((-260, -620, S + 260, 470), fill=(255, 255, 255, 52))
shine = shine.filter(ImageFilter.GaussianBlur(18))
base = Image.alpha_composite(base, shine)

# Edge light: bright top-left, cyan bottom-right, as on a glass slab.
ring = Image.new('RGBA', (S, S), (0, 0, 0, 0))
outer = mask
inner = squircle_mask(S, 66)
rim_mask = ImageChops.subtract(outer, inner)
lit = gradient(S, [(0, (255, 255, 255)), (.4, (170, 215, 255)), (.7, (110, 160, 220)), (1, (140, 255, 235))], 45).convert('RGBA')
lit.putalpha(rim_mask.point(lambda v: int(v * .85)))
base = Image.alpha_composite(base, lit)

base.putalpha(mask)

# Tile shadow so the icon floats on any background.
canvas = Image.new('RGBA', (S, S), (0, 0, 0, 0))
drop = Image.new('RGBA', (S, S), (3, 8, 28, 0))
drop.putalpha(mask.filter(ImageFilter.GaussianBlur(26)).point(lambda v: int(v * .5)))
canvas = Image.alpha_composite(canvas, ImageChops.offset(drop, 0, 18))
canvas = Image.alpha_composite(canvas, base)

r_, g_, b_, a_ = canvas.split()
gray = Image.merge('RGBA', (*(canvas.convert('L'),) * 3, a_))
canvas = gray
canvas.resize((256, 256), Image.Resampling.LANCZOS).save(root / 'RelayProxy' / 'relay.png')
canvas.save(root / 'RelayProxy' / 'relay.ico', sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print('icon ok')
