"""Build the six-terrain contact sheet: 1920 px wide, two columns of three frames,
each cell labelled with the frame name and the camera it was rendered from.

    python make-stills-sheet.py --dir artifacts/stills/verify-run \
        --out artifacts/stills/verify-run/six-terrain-sheet.png \
        --labels labels.json

labels.json is {"desert": "cam=(...) fov=40 origin=table", ...}; the sheet is a pure
function of the six pngs plus that text, so two runs that produce identical frames
produce an identical sheet (byte for byte) - which is how "-Repeat" can include the
sheet in its pixel-diff-0 evidence.
"""
import argparse
import json
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

FRAMES = ["desert", "ocean", "forest", "steppe", "snow", "mountain"]
WIDTH = 1920
COLS = 2
ROWS = 3
LABEL_H = 28
GAP = 4
BORDER = 2
BG = (18, 18, 20)
INK = (232, 232, 228)
ACCENT = (150, 190, 160)


def load_font(size):
    for candidate in ("C:/Windows/Fonts/consola.ttf", "C:/Windows/Fonts/arial.ttf"):
        try:
            return ImageFont.truetype(candidate, size)
        except OSError:
            continue
    return ImageFont.load_default()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--labels", default="", help="json {frame: text}")
    args = ap.parse_args()

    src = Path(args.dir)
    labels = {}
    if args.labels:
        # PowerShell's Set-Content -Encoding UTF8 writes a BOM on 5.1
        labels = json.loads(Path(args.labels).read_text(encoding="utf-8-sig"))

    imgs = {}
    for name in FRAMES:
        p = src / f"{name}.png"
        if not p.exists():
            print(f"MISSING {p}")
            return 2
        imgs[name] = Image.open(p).convert("RGB")

    sizes = {name: im.size for name, im in imgs.items()}
    if len(set(sizes.values())) != 1:
        print(f"frames have different sizes: {sizes}")
        return 2
    fw, fh = sizes[FRAMES[0]]

    cell_w = (WIDTH - GAP * (COLS + 1)) // COLS
    scale = cell_w / fw
    cell_h = int(round(fh * scale))

    header_h = 22
    sheet_h = header_h + GAP + ROWS * (cell_h + LABEL_H + GAP)
    sheet = Image.new("RGB", (WIDTH, sheet_h), BG)
    draw = ImageDraw.Draw(sheet)
    title_font = load_font(17)
    label_font = load_font(15)

    # header
    draw.text((GAP + 2, 4), "ai003 - six terrain stills (1920x804 each)", font=title_font, fill=ACCENT)

    for idx, name in enumerate(FRAMES):
        r, c = divmod(idx, COLS)
        x = GAP + c * (cell_w + GAP)
        y = header_h + GAP + r * (cell_h + LABEL_H + GAP)
        sheet.paste(imgs[name].resize((cell_w, cell_h), Image.LANCZOS), (x, y))
        draw.rectangle([x - BORDER, y - BORDER, x + cell_w + BORDER - 1, y + cell_h + BORDER - 1],
                       outline=(60, 60, 64), width=BORDER)
        label = labels.get(name, "")
        draw.text((x, y + cell_h + 5), name, font=label_font, fill=INK)
        if label:
            draw.text((x + 78, y + cell_h + 5), label, font=label_font, fill=(168, 168, 164))

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(out)
    print(f"sheet: {out}  {sheet.size[0]}x{sheet.size[1]}  cells={cell_w}x{cell_h}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
