"""Draws the Keystone app icon and writes Assets/keystone.ico and Assets/keystone.png.

A stone arch on an emerald tile, held together by a gold keystone at its crown: the piece that
keeps everything else in place, like the app does for a household.
Usage: python3 scripts/make-logo.py
"""
import math
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

ASSETS = Path(__file__).resolve().parent.parent / "src" / "Keystone" / "Assets"
SCALE = 4  # Draw large and downsample for smooth edges.
SIZE = 1024

STONE = (240, 253, 250, 240)  # mint white
KEYSTONE = (251, 191, 36, 255)  # warm gold
KEYSTONE_SHADE = (217, 119, 6, 255)  # darker gold for its lower face


def gradient(size, top, bottom):
    """Diagonal gradient from top-left to bottom-right."""
    w, h = size
    img = Image.new("RGB", size)
    px = img.load()
    for y in range(h):
        for x in range(w):
            t = (x * 0.35 + y) / (w * 0.35 + h)
            px[x, y] = tuple(round(a + (b - a) * t) for a, b in zip(top, bottom))
    return img


def wedge(cx, cy, r_in, r_out, a_from, a_to, steps=48):
    """An arch stone: the ring between two radii and two angles (degrees, 0 = right, counter-clockwise)."""
    outer = [(cx + r_out * math.cos(math.radians(a)), cy - r_out * math.sin(math.radians(a)))
             for a in (a_from + (a_to - a_from) * i / steps for i in range(steps + 1))]
    inner = [(cx + r_in * math.cos(math.radians(a)), cy - r_in * math.sin(math.radians(a)))
             for a in (a_to + (a_from - a_to) * i / steps for i in range(steps + 1))]
    return outer + inner


def draw_icon():
    s = SIZE * SCALE
    u = lambda v: v * SCALE  # Coordinates below are in 1024 space.
    canvas = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # macOS tile: 824px rounded square centred on a 1024 canvas.
    inset, radius = 100 * SCALE, 185 * SCALE
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle((inset, inset, s - inset, s - inset), radius, fill=255)

    bg = gradient((s // 8, s // 8), (16, 185, 129), (6, 60, 70)).resize((s, s), Image.BICUBIC)
    canvas.paste(bg, (0, 0), mask)

    layer = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)

    cx, cy = 512, 614  # centre of the arch's circle
    r_in, r_out = 168, 300
    gap = 3.2  # degrees between stones

    # Seven stones over the half circle; the middle one is the keystone.
    count = 7
    span = 180 / count
    for i in range(count):
        a_from = i * span + gap / 2
        a_to = (i + 1) * span - gap / 2
        if i == count // 2:
            continue
        d.polygon([(u(x), u(y)) for x, y in wedge(cx, cy, r_in, r_out, a_from, a_to)], fill=STONE)

    # Pillars the arch stands on.
    pillar_top, pillar_bottom = cy + 14, 792
    for x0, x1 in ((cx - r_out, cx - r_in), (cx + r_in, cx + r_out)):
        d.rounded_rectangle((u(x0), u(pillar_top), u(x1), u(pillar_bottom)), u(10), fill=STONE)

    # Keystone: fills its slot at the crown, with sides on the arch's radial lines, but reaches
    # further in and out than the other stones, with a flat top.
    k_from, k_to = 90 - span / 2 + gap / 2, 90 + span / 2 - gap / 2
    k_in, k_out = r_in - 30, r_out + 64
    top_y = cy - k_out

    def on_ray(angle, y):
        """The point at height y on the line from the arch's centre at this angle."""
        return (cx + (cy - y) / math.tan(math.radians(angle)), y)

    left_out, right_out = on_ray(k_to, top_y), on_ray(k_from, top_y)
    left_in = (cx + k_in * math.cos(math.radians(k_to)), cy - k_in * math.sin(math.radians(k_to)))
    right_in = (cx + k_in * math.cos(math.radians(k_from)), cy - k_in * math.sin(math.radians(k_from)))

    # Soft shadow under the keystone so it reads as the piece in front.
    shadow = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).polygon(
        [(u(x), u(y + 14)) for x, y in (left_out, right_out, right_in, left_in)], fill=(2, 40, 34, 110))
    layer = Image.alpha_composite(layer, shadow.filter(ImageFilter.GaussianBlur(u(10))))
    d = ImageDraw.Draw(layer)

    d.polygon([(u(x), u(y)) for x, y in (left_out, right_out, right_in, left_in)], fill=KEYSTONE)
    # A darker lower band gives the stone some depth.
    band = 0.28
    lerp = lambda p, q, t: (p[0] + (q[0] - p[0]) * t, p[1] + (q[1] - p[1]) * t)
    d.polygon(
        [(u(x), u(y)) for x, y in (lerp(left_in, left_out, band), lerp(right_in, right_out, band), right_in, left_in)],
        fill=KEYSTONE_SHADE,
    )

    layer.putalpha(ImageChops.multiply(layer.getchannel("A"), mask))
    canvas = Image.alpha_composite(canvas, layer)
    return canvas.resize((SIZE, SIZE), Image.LANCZOS)


def main():
    icon = draw_icon()
    icon.save(ASSETS / "keystone.png")
    # Windows and the window title bar use the .ico; crop the macOS padding so it fills the square.
    tight = icon.crop((100, 100, 924, 924)).resize((256, 256), Image.LANCZOS)
    tight.save(ASSETS / "keystone.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print(f"Wrote {ASSETS / 'keystone.png'} and {ASSETS / 'keystone.ico'}")


if __name__ == "__main__":
    main()
