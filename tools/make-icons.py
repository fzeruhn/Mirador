"""Draws Mirador's icons (src/Mirador.App/Assets/*.ico, and the README's .github/mirador.png): Monado's hexagon set
into the Windows logo's four panes. Needs Pillow: pip install pillow.

The hexagon and its M are the Monado logo from Monado's Android launcher icon
(src/xrt/targets/openxr_android/src/main/res/drawable/ic_logo_launcher.xml), Copyright 2023, Collabora, Ltd.,
CC BY 4.0 (https://creativecommons.org/licenses/by/4.0/); here it is recoloured and set over the panes. The panes
follow Microsoft's 2021 Windows logo: square corners, gaps 253/4875 of its width.
"""
import re
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw

REPO = Path(__file__).resolve().parent.parent
ASSETS = REPO / "src" / "Mirador.App" / "Assets"
README_IMAGE = REPO / ".github" / "mirador.png"  # shown at 128, so 256 stays sharp on high-DPI screens

# The design is laid out on a 256 grid, drawn at 1024 and scaled down to each icon size.
BIG = 1024
UNIT = BIG / 256

# The panes: four squares in 20..236, 11.21 apart.
PANE_AT = (20, 133.61)
PANE = 102.39
PANES_BOX = (20, 20, 236, 236)

# Monado's logo, in its own 144 grid, centred on the icon at twice its size. The see-through ring along the hexagon's
# edge is as wide as the gaps between the panes.
HEXAGON = (
    "M104.67,46.75 L74.16,37.56c-1.41,-0.42 -2.91,-0.42 -4.32,0l-30.51,9.19c-2.14,0.64 -3.6,2.62 -3.6,4.85v40.95"
    "c0,2.24 1.48,4.22 3.63,4.86l30.53,9.03c1.38,0.41 2.85,0.41 4.23,0l30.53,-9.03c2.15,-0.64 3.63,-2.61 3.63,-4.86"
    "L108.28,51.6c0,-2.24 -1.46,-4.21 -3.6,-4.85z"
)
LETTER_M = (
    "M59.32,87.31 L50.66,84.73C49.45,84.37 48.62,83.26 48.62,81.99L48.62,64.27l10.7,14.71v8.32z"
    "M72,89.31L48.62,57.39 57.28,54.81c1.13,-0.34 2.35,0.06 3.07,0.99L72,70.89 83.64,55.8c0.72,-0.93 1.94,-1.33 3.07,-0.99"
    "l8.66,2.58z"
    "M95.38,81.99c0,1.26 -0.83,2.38 -2.04,2.74L84.68,87.31L84.68,78.99L95.38,64.27v17.72z"
)
LOGO_CENTRE = (72, 72.2)
LOGO_SCALE = 2
RING = 11.21

# Colours, top-left to bottom-right.
BLUE = ((0x34, 0xA6, 0xF8), (0x00, 0x46, 0x8C))
PURPLE = ((0xB4, 0x61, 0xD8), (0x46, 0x11, 0x56))
RED = ((0xF4, 0x5B, 0x66), (0x8C, 0x1A, 0x26))
GREY_PANES = ((0xB0, 0xB0, 0xB0), (0x6E, 0x6E, 0x6E))
GREY_HEXAGON = ((0x80, 0x80, 0x80), (0x40, 0x40, 0x40))
WHITE = (255, 255, 255, 255)


def outlines(path):
    """The closed outlines of an SVG path (M, L, H, V, C, Z; absolute or relative) as point lists, curves flattened."""
    tokens = re.findall(r"[MLHVCZmlhvcz]|-?(?:\d+\.?\d*|\.\d+)", path)
    found, points, cmd, i = [], [], None, 0
    x = y = 0.0

    def number():
        nonlocal i
        i += 1
        return float(tokens[i - 1])

    while i < len(tokens):
        if tokens[i].isalpha():
            cmd = tokens[i]
            i += 1
            if cmd in "Zz":
                x, y = points[0]
                found.append(points)
                points = []
                continue
        dx, dy = (x, y) if cmd.islower() else (0.0, 0.0)
        kind = cmd.upper()
        if kind == "M":
            x, y = dx + number(), dy + number()
            points = [(x, y)]
            cmd = "l" if cmd.islower() else "L"
        elif kind == "L":
            x, y = dx + number(), dy + number()
            points.append((x, y))
        elif kind == "H":
            x = dx + number()
            points.append((x, y))
        elif kind == "V":
            y = dy + number()
            points.append((x, y))
        elif kind == "C":
            c1 = (dx + number(), dy + number())
            c2 = (dx + number(), dy + number())
            end = (dx + number(), dy + number())
            start = (x, y)
            for step in range(1, 25):
                t = step / 24
                a, b, c, d = (1 - t) ** 3, 3 * (1 - t) ** 2 * t, 3 * (1 - t) * t * t, t ** 3
                points.append(tuple(a * start[k] + b * c1[k] + c * c2[k] + d * end[k] for k in range(2)))
            x, y = end
    return found


def placed(path):
    """The path's outlines, from the logo's grid to the 1024 drawing."""
    (lx, ly), s = LOGO_CENTRE, LOGO_SCALE * UNIT
    return [[(BIG / 2 + (px - lx) * s, BIG / 2 + (py - ly) * s) for px, py in o] for o in outlines(path)]


def gradient(colours, box):
    """A full-size layer coloured from the box's top-left corner to its bottom-right, like an SVG linear gradient
    from (0,0) to (1,1) in the box's own units."""
    x0, y0, x1, y1 = (round(v) for v in box)
    down = Image.linear_gradient("L").resize((x1 - x0, y1 - y0))
    across = Image.linear_gradient("L").transpose(Image.Transpose.TRANSPOSE).resize((x1 - x0, y1 - y0))
    t = ImageChops.add(across, down, scale=2.0)
    (r0, g0, b0), (r1, g1, b1) = colours
    rgb = Image.merge("RGB", [t.point(lambda v, a=a, b=b: round(a + (b - a) * v / 255)) for a, b in ((r0, r1), (g0, g1), (b0, b1))])
    layer = Image.new("RGB", (BIG, BIG), colours[1])  # outside the box is masked off by the caller
    layer.paste(rgb, (x0, y0))
    return layer


def mask(draw_on):
    m = Image.new("L", (BIG, BIG), 0)
    draw_on(ImageDraw.Draw(m))
    return m


def stroke(d, points, width):
    """A closed outline stroked with round joins: a band along each segment and a disc on each point. (Pillow's own
    wide line leaves specks at the joins of a flattened curve.)"""
    r = width / 2
    for (x0, y0), (x1, y1) in zip(points, points[1:] + points[:1]):
        length = ((x1 - x0) ** 2 + (y1 - y0) ** 2) ** 0.5
        if length:
            nx, ny = -(y1 - y0) / length * r, (x1 - x0) / length * r
            d.polygon([(x0 + nx, y0 + ny), (x1 + nx, y1 + ny), (x1 - nx, y1 - ny), (x0 - nx, y0 - ny)], fill=255)
        d.ellipse((x0 - r, y0 - r, x0 + r, y0 + r), fill=255)


def icon(panes, hexagon, centre):
    """The 1024 image: the panes, the hexagon over them with a see-through ring, and the white M or "!" on top."""
    hexagon_points = placed(HEXAGON)[0]
    xs, ys = [p[0] for p in hexagon_points], [p[1] for p in hexagon_points]

    pane_mask = mask(lambda d: [d.rectangle((x * UNIT, y * UNIT, (x + PANE) * UNIT - 1, (y + PANE) * UNIT - 1), fill=255)
                                for x in PANE_AT for y in PANE_AT])
    hexagon_mask = mask(lambda d: d.polygon(hexagon_points, fill=255))
    ring_mask = mask(lambda d: stroke(d, hexagon_points, RING * UNIT))

    img = gradient(panes, [v * UNIT for v in PANES_BOX]).convert("RGBA")
    img.paste(gradient(hexagon, (min(xs), min(ys), max(xs), max(ys))), (0, 0), hexagon_mask)
    img.putalpha(ImageChops.multiply(ImageChops.lighter(pane_mask, hexagon_mask), ImageChops.invert(ring_mask)))

    d = ImageDraw.Draw(img)
    if centre == "M":
        for outline in placed(LETTER_M):
            d.polygon(outline, fill=WHITE)
    else:
        d.rounded_rectangle([v * UNIT for v in (118, 82, 138, 144)], radius=5 * UNIT, fill=WHITE)
        d.ellipse([v * UNIT for v in (117, 151, 139, 173)], fill=WHITE)
    return img


ICONS = {
    "Mirador.ico": (BLUE, PURPLE, "M"),
    "TrayRunning.ico": (BLUE, PURPLE, "M"),
    "TrayStopped.ico": (GREY_PANES, GREY_HEXAGON, "M"),
    "TrayProblem.ico": (GREY_PANES, RED, "!"),
    "TrayRunningProblem.ico": (BLUE, RED, "!"),
}
APP_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
TRAY_SIZES = [16, 20, 24, 32, 40, 48]


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    for name, look in ICONS.items():
        sizes = APP_SIZES if name == "Mirador.ico" else TRAY_SIZES
        icon(*look).save(ASSETS / name, format="ICO", sizes=[(s, s) for s in sizes])
    README_IMAGE.parent.mkdir(parents=True, exist_ok=True)
    icon(*ICONS["Mirador.ico"]).resize((256, 256), Image.Resampling.LANCZOS).save(README_IMAGE, optimize=True)


if __name__ == "__main__":
    main()
