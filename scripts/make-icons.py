"""Generates the app icon (a gauge on a terracotta tile) for every client.
Run: python3 scripts/make-icons.py   (needs Pillow)"""
import math, os
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BG = (201, 100, 66, 255)       # terracotta
FG = (255, 250, 245, 255)
DIM = (255, 250, 245, 110)

def gauge(size: int, pct: float = 0.62) -> Image.Image:
    s = size * 4
    im = Image.new('RGBA', (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    r = int(s * 0.22)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=r, fill=BG)
    cx, cy = s / 2, s * 0.60
    R = s * 0.32
    w = max(4, int(s * 0.085))
    box = [cx - R, cy - R, cx + R, cy + R]
    d.arc(box, 180, 360, fill=DIM, width=w)
    d.arc(box, 180, 180 + 180 * pct, fill=FG, width=w)
    # rounded caps (dim track end first, then the filled part on top)
    a = math.radians(360)
    x, y = cx + (R - w / 2) * math.cos(a), cy + (R - w / 2) * math.sin(a)
    d.ellipse([x - w / 2, y - w / 2, x + w / 2, y + w / 2], fill=DIM)
    for ang in (180, 180 + 180 * pct):
        a = math.radians(ang)
        x, y = cx + (R - w / 2) * math.cos(a), cy + (R - w / 2) * math.sin(a)
        d.ellipse([x - w / 2, y - w / 2, x + w / 2, y + w / 2], fill=DIM if ang == 180 and pct == 0 else FG)
    # needle
    a = math.radians(180 + 180 * pct)
    L = R * 0.78
    nw = max(3, int(s * 0.05))
    d.line([cx, cy, cx + L * math.cos(a), cy + L * math.sin(a)], fill=FG, width=nw)
    hub = s * 0.065
    d.ellipse([cx - hub, cy - hub, cx + hub, cy + hub], fill=FG)
    return im.resize((size, size), Image.LANCZOS)

def save(path, size):
    full = os.path.join(ROOT, path)
    os.makedirs(os.path.dirname(full), exist_ok=True)
    gauge(size).save(full)
    print('wrote', path)

save('vscode-extension/media/icon.png', 128)
for n in (16, 32, 48, 128):
    save(f'browser-extension/icons/icon{n}.png', n)
save('tray-app/build/icon.png', 1024)
save('docs/icon.png', 256)
