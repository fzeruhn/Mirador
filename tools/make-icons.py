"""Draws Mirador's icons (src/Mirador.App/Assets/*.ico): an eye, for a lookout. Needs Pillow: pip install pillow."""
from pathlib import Path
from PIL import Image, ImageDraw

ASSETS = Path(__file__).resolve().parent.parent / "src" / "Mirador.App" / "Assets"
BIG = 512

BLUE = (45, 127, 249, 255)
GREY = (138, 138, 138, 255)
RED = (220, 53, 69, 255)
WHITE = (255, 255, 255, 255)


def eye(color, badge=None):
    img = Image.new("RGBA", (BIG, BIG), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    c = BIG / 2
    half_width, half_height, stroke = BIG * 0.43, BIG * 0.27, BIG * 0.085
    # The outline: an almond, pointed left and right, drawn as one closed stroke.
    steps = 64
    top = [(c + half_width * t, c - half_height * (1 - t * t)) for t in (i / steps * 2 - 1 for i in range(steps + 1))]
    bottom = [(x, 2 * c - y) for x, y in reversed(top)]
    d.line(top + bottom[1:] + [top[1]], fill=color, width=round(stroke), joint="curve")
    # The pupil.
    r = BIG * 0.15
    d.ellipse((c - r, c - r, c + r, c + r), fill=color)
    if badge:
        br = BIG * 0.21
        bx, by = BIG - br - BIG * 0.02, BIG - br - BIG * 0.02
        gap = BIG * 0.05
        d.ellipse((bx - br - gap, by - br - gap, bx + br + gap, by + br + gap), fill=(0, 0, 0, 0))
        d.ellipse((bx - br, by - br, bx + br, by + br), fill=badge)
        d.rectangle((bx - BIG * 0.025, by - br * 0.6, bx + BIG * 0.025, by + br * 0.15), fill=WHITE)
        d.ellipse((bx - BIG * 0.03, by + br * 0.32, bx + BIG * 0.03, by + br * 0.32 + BIG * 0.06), fill=WHITE)
    return img


def save(img, name, sizes):
    img.save(ASSETS / name, format="ICO", sizes=[(s, s) for s in sizes])


ASSETS.mkdir(parents=True, exist_ok=True)
save(eye(BLUE), "Mirador.ico", [16, 20, 24, 32, 40, 48, 64, 256])
tray = [16, 20, 24, 32, 40, 48]
save(eye(GREY), "TrayStopped.ico", tray)
save(eye(BLUE), "TrayRunning.ico", tray)
save(eye(GREY, badge=RED), "TrayProblem.ico", tray)
