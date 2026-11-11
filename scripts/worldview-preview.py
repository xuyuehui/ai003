#!/usr/bin/env python3
"""Look-dev previews for the Ashkar (鸣沙) worldview.

Reads the *existing* baked world (same bytes both engines load) and re-themes it
with the desert-planet palette, so the design doc can show what the same terrain
reads like before any generator change. Writes:

  docs/images/worldview/ashkar-basin.png   top-down hillshaded map + place names
  docs/images/worldview/palette.png        colour script / palette board

Usage:  python scripts/worldview-preview.py
"""

from __future__ import annotations

import json
import math
import os
from dataclasses import dataclass

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORLD = os.path.join(ROOT, "world")
OUT = os.path.join(ROOT, "docs", "images", "worldview")

FONT_CN = r"C:\Windows\Fonts\msyh.ttc"
FONT_CN_BOLD = r"C:\Windows\Fonts\msyhbd.ttc"
FONT_MONO = r"C:\Windows\Fonts\consola.ttf"

# ---------------------------------------------------------------- palette ---
# sRGB 0-255. Names match docs/worldview/03-art-direction.md.
PALETTE = {
    "sand_lit": (243, 214, 164),
    "sand": (211, 169, 116),
    "sand_dark": (156, 116, 76),
    "salt": (238, 232, 220),
    "salt_shade": (198, 191, 176),
    "reg": (168, 147, 118),
    "reg_dark": (120, 104, 80),
    "basalt": (43, 38, 33),
    "basalt_lit": (84, 75, 66),
    "rust": (118, 72, 51),
    "frost": (230, 236, 238),
    "coldfire": (108, 226, 201),
    "brass": (176, 138, 79),
    "bone": (237, 230, 216),
    "ember": (194, 69, 45),
    "sky": (206, 203, 193),
    "ink": (26, 24, 21),
}


# ------------------------------------------------------------- world data ---
def load_world():
    with open(os.path.join(WORLD, "world.json"), "r", encoding="utf-8") as f:
        meta = json.load(f)

    res = meta["resolution"]
    h = np.fromfile(os.path.join(WORLD, "heightmap.f32"), dtype="<f4").reshape(res, res)
    b = np.fromfile(os.path.join(WORLD, "biomes.r8"), dtype=np.uint8).reshape(res, res)
    return meta, h.astype(np.float64), b


def normals(h: np.ndarray, cell: float) -> np.ndarray:
    gy, gx = np.gradient(h, cell)
    n = np.stack([-gx, np.ones_like(h), -gy], axis=-1)
    return n / np.linalg.norm(n, axis=-1, keepdims=True)


def box_mean(a: np.ndarray, r: int) -> np.ndarray:
    """Mean over a (2r+1)^2 window via summed-area table."""
    p = np.pad(a, ((r + 1, r), (r + 1, r)), mode="edge")
    s = p.cumsum(0).cumsum(1)
    k = 2 * r + 1
    return (s[k:, k:] - s[:-k, k:] - s[k:, :-k] + s[:-k, :-k]) / (k * k)


def find_point(mask: np.ndarray, radius: int) -> tuple[int, int]:
    """Centre of the densest window of `mask` -> (x, y) pixel."""
    dens = box_mean(mask.astype(np.float64), radius)
    y, x = np.unravel_index(int(np.argmax(dens)), dens.shape)
    return int(x), int(y)


# ---------------------------------------------------------------- shading ---
def base_colors(h, biomes, meta):
    """Biome -> sRGB, with altitude and relief modulation. h in metres."""
    res = h.shape[0]
    sea = meta["seaLevel"]
    hmax = max(meta["maxHeight"], 1.0)

    grass = biomes == 2
    desert = biomes == 3
    rock = biomes == 4
    snow = biomes == 5
    ocean = biomes == 0
    beach = biomes == 1

    img = np.zeros((res, res, 3), dtype=np.float64)

    def put(mask, col):
        img[mask] = col

    # 砾漠 / reg: the old grassland becomes wind-scoured gravel.
    put(grass, PALETTE["reg"])
    put(beach, mix_col(PALETTE["salt"], PALETTE["sand"], 0.45))
    put(desert, PALETTE["sand"])
    put(rock, PALETTE["basalt"])
    put(snow, PALETTE["frost"])

    # Salt basin: bright salt crust, darker where it is deepest.
    depth = np.clip(-h / 70.0, 0, 1)
    salt = lerp_cols(PALETTE["salt"], PALETTE["salt_shade"], depth[..., None])
    img[ocean] = salt[ocean]

    # Sand seas get lighter with altitude (wind-winnowed crests).
    alt = np.clip(h / hmax, 0, 1)[..., None]
    img[desert] = (lerp_cols(PALETTE["sand_dark"], PALETTE["sand_lit"], alt)[desert])

    # Basalt picks up rust in the low, iron-rich shelves.
    rustw = np.clip((160.0 - h) / 220.0, 0, 0.38)[..., None]
    img[rock] = (lerp_cols(PALETTE["basalt"], PALETTE["rust"], rustw)[rock])



    # Fine mottling everywhere so flat areas do not read as plastic.
    rng = np.random.default_rng(7)
    mot = rng.normal(0.0, 1.0, (res, res))
    mot = box_mean(mot, 2) * 0.9 + rng.normal(0.0, 0.35, (res, res))
    img *= (1.0 + np.clip(mot, -1, 1)[..., None] * 0.035)

    # Wind ripples on sand only: fine anisotropic sine, rotated ~28 degrees.
    yy, xx = np.mgrid[0:res, 0:res].astype(np.float64)
    ang = math.radians(28.0)
    rip = np.sin((xx * math.cos(ang) + yy * math.sin(ang)) * 1.15)
    img[desert] *= (1.0 + rip[desert][:, None] * 0.028)

    return np.clip(img, 0, 255)


def rng_crust(res: int, seed: int) -> np.ndarray:
    return np.random.default_rng(seed).random((res, res))


def worley_seams(res: int, cells: int = 34, seed: int = 5) -> np.ndarray:
    """0..1 seam mask (1 on the polygon boundary) for salt-crust cracks."""
    rng = np.random.default_rng(seed)
    pts = (np.stack(np.mgrid[0:cells, 0:cells], -1).reshape(-1, 2) + 0.5
           + rng.uniform(-0.38, 0.38, (cells * cells, 2))) / cells * res
    pts = pts.reshape(cells, cells, 2)

    ys, xs = np.mgrid[0:res, 0:res]
    fx = (xs + 0.5) / res * cells
    fy = (ys + 0.5) / res * cells
    cx = np.floor(fx).astype(int)
    cy = np.floor(fy).astype(int)

    d1 = np.full((res, res), 1e9)
    d2 = np.full((res, res), 1e9)
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            nx = np.clip(cx + dx, 0, cells - 1)
            ny = np.clip(cy + dy, 0, cells - 1)
            px = pts[ny, nx, 0] - 0.5 - (xs + 0.5)
            py = pts[ny, nx, 1] - 0.5 - (ys + 0.5)
            dist = np.hypot(px, py)
            d2 = np.minimum(d2, np.maximum(d1, dist))
            d1 = np.minimum(d1, dist)

    seam = d2 - d1
    return np.clip(1.0 - seam / 1.1, 0, 1) ** 2


def mix_col(a, b, t):
    return tuple(a[i] * (1 - t) + b[i] * t for i in range(3))


def lerp_cols(a, b, t):
    a = np.array(a, dtype=np.float64)
    b = np.array(b, dtype=np.float64)
    return a * (1 - t) + b * t


def shade(img, n, h, meta):
    """Two suns: a high white key and a low rust fill, plus ambient sky."""
    res = img.shape[0]
    # Key sun, north-west, 42 degrees up.
    az, el = math.radians(-35.0), math.radians(42.0)
    key = np.array([math.cos(el) * math.sin(az), math.sin(el), math.cos(el) * math.cos(az)])
    # Secondary sun, south-east, 16 degrees up, dimmer and warmer.
    az2, el2 = math.radians(150.0), math.radians(16.0)
    fill = np.array([math.cos(el2) * math.sin(az2), math.sin(el2), math.cos(el2) * math.cos(az2)])

    kd = np.clip((n * key).sum(-1), 0, 1)[..., None]
    fd = np.clip((n * fill).sum(-1), 0, 1)[..., None]

    amb = np.array(PALETTE["sky"], dtype=np.float64) / 255.0
    col = img / 255.0
    out = col * (0.34 + 0.78 * kd)
    out += col * fd * np.array([1.0, 0.72, 0.52]) * 0.16
    out += amb * 0.10 * (1.0 - kd)

    # Salt flats bounce a lot of light: lift their ambient.
    out = np.clip(out, 0, 1)

    # Aerial perspective: low ground hazes toward the dust sky.
    haze = np.clip((30.0 - h) / 140.0, 0, 0.35)[..., None]
    out = out * (1 - haze) + amb * haze

    return np.clip(out * 255, 0, 255).astype(np.uint8)


# --------------------------------------------------------------- compose ---
@dataclass
class Label:
    text: str
    sub: str
    px: float
    py: float
    bx: float
    by: float


def draw_leader(d: ImageDraw.ImageDraw, x0, y0, x1, y1, col):
    d.line([(x0, y0), (x1, y1)], fill=col, width=2)
    d.ellipse([x0 - 4, y0 - 4, x0 + 4, y0 + 4], outline=col, width=2)


def main():
    os.makedirs(OUT, exist_ok=True)
    meta, h, biomes = load_world()
    res = h.shape[0]
    cell = meta["cellSize"]

    base = base_colors(h, biomes, meta)
    n = normals(h, cell)
    rgb = shade(base, n, h, meta)

    # Upscale with a mild sharpen; terrain data is 4 m/vertex.
    size = 1400
    img = Image.fromarray(rgb, "RGB").resize((size, size), Image.BICUBIC)
    img = img.filter(ImageFilter.UnsharpMask(radius=2.6, percent=40, threshold=3))

    # Vignette + paper grain.
    arr = np.asarray(img).astype(np.float64)
    yy, xx = np.mgrid[0:size, 0:size]
    r = np.sqrt(((xx - size / 2) / (size / 2)) ** 2 + ((yy - size / 2) / (size / 2)) ** 2)
    vig = np.clip(1.06 - 0.16 * np.clip(r - 0.45, 0, None) ** 1.4, 0, 1)
    rng = np.random.default_rng(11)
    grain = rng.normal(0, 1.6, (size, size, 1))
    arr = np.clip(arr * vig[..., None] + grain, 0, 255).astype(np.uint8)
    img = Image.fromarray(arr, "RGB")

    # Canvas with header / footer.
    W, H = size + 120, size + 330
    canvas = Image.new("RGB", (W, H), PALETTE["ink"])
    canvas.paste(img, (60, 150))

    d = ImageDraw.Draw(canvas)
    f_title = ImageFont.truetype(FONT_CN_BOLD, 46)
    f_sub = ImageFont.truetype(FONT_CN, 22)
    f_lab = ImageFont.truetype(FONT_CN_BOLD, 25)
    f_small = ImageFont.truetype(FONT_CN, 18)
    f_mono = ImageFont.truetype(FONT_MONO, 19)

    bone = tuple(PALETTE["bone"]) + (255,)
    dim = (150, 145, 134, 255)

    d.text((60, 42), "阿什卡 · 白喉盆地", font=f_title, fill=bone)
    d.text((60, 104), "ASHKAR  /  WHITE THROAT BASIN   ·   同一份地形数据，换沙漠行星调色板",
           font=f_sub, fill=dim)

    # ---- place labels -------------------------------------------------
    cx0, cy0 = 60, 150
    to_px = lambda x, z: ((x + meta["sizeMeters"] / 2) / cell / (res - 1) * size + cx0,
                          (z + meta["sizeMeters"] / 2) / cell / (res - 1) * size + cy0)

    sx, sy = to_px(meta["spawn"]["x"], meta["spawn"]["z"])

    valid = np.zeros_like(h, dtype=bool)
    valid[70:-70, 70:-70] = True
    gy, gx = np.mgrid[0:res, 0:res]
    tr = (gx > res * 0.72) & (gx < res * 0.90) & (gy > res * 0.10) & (gy < res * 0.30)
    bl = (gx < res * 0.45) & (gy > res * 0.55)
    mid = (abs(gx - res / 2) < res * 0.22) & (abs(gy - res / 2) < res * 0.22)

    salt_xy = find_point((biomes == 0) & (h < -15) & valid & bl, 18)
    sand_xy = find_point((biomes == 3) & valid & tr, 26)
    rock_xy = find_point((biomes == 4) & (h > 120) & valid & mid, 16)
    snow_xy = find_point((biomes == 5) & valid & mid, 12)

    def to_canvas(p):
        return (p[0] / (res - 1) * size + cx0, p[1] / (res - 1) * size + cy0)

    print("anchors:", to_canvas(sand_xy), to_canvas(salt_xy), to_canvas(rock_xy),
          to_canvas(snow_xy), (round(sx), round(sy)))

    # Boxes live in map space (0..size); anchors are the actual feature pixels.
    labels = [
        Label("长眠海", "THE LONG SLEEP · 沙海", *to_canvas(sand_xy),
              bx=cx0 + 860, by=cy0 + 220),
        Label("白喉盐海", "WHITE THROAT · 干盐湖", *to_canvas(salt_xy),
              bx=cx0 + 150, by=cy0 + 1040),
        Label("骨脊", "THE SPINE · 玄武岩脊", *to_canvas(rock_xy),
              bx=cx0 + 200, by=cy0 + 470),
        Label("霜冠", "FROST CROWN · 极区盐霜", *to_canvas(snow_xy),
              bx=cx0 + 780, by=cy0 + 1010),
        Label("第一井 · 出生点", "WELL ONE · SPAWN", sx, sy,
              bx=cx0 + 480, by=cy0 + 700),
    ]

    for lab in labels:
        tw = d.textlength(lab.text, font=f_lab)
        sw = d.textlength(lab.sub, font=f_small)
        bw = max(tw, sw) + 24
        bh = 62
        # Leader leaves the box on the side facing the anchor.
        ax, ay = lab.px, lab.py
        bx = lab.bx
        by = lab.by
        ex = bx + bw if ax > bx + bw else bx
        ey = ay if by <= ay <= by + bh else (by + bh / 2)
        draw_leader(d, ax, ay, ex, ey, (240, 232, 214, 170))
        d.rectangle([bx, by, bx + bw, by + bh], fill=(16, 14, 12, 205))
        d.rectangle([bx, by, bx + bw, by + bh], outline=(196, 186, 166, 120))
        d.text((bx + 12, by + 5), lab.text, font=f_lab, fill=bone)
        d.text((bx + 12, by + 34), lab.sub, font=f_small, fill=dim)

    # ---- scale bar ----------------------------------------------------
    bar_m = 500
    bar_px = bar_m / meta["sizeMeters"] * size
    bx0, by0 = 60 + 24, 150 + size - 46
    d.line([(bx0, by0), (bx0 + bar_px, by0)], fill=bone, width=3)
    for xx in (bx0, bx0 + bar_px):
        d.line([(xx, by0 - 6), (xx, by0 + 6)], fill=bone, width=3)
    d.text((bx0, by0 - 30), "500 m", font=f_mono, fill=bone)

    # ---- legend / footer ---------------------------------------------
    fy = 150 + size + 26
    d.text((60, fy), "图例", font=f_lab, fill=bone)
    legend = [
        ("沙海", PALETTE["sand"]),
        ("盐海", PALETTE["salt"]),
        ("砾漠", PALETTE["reg"]),
        ("玄武岩", PALETTE["basalt"]),
        ("盐霜", PALETTE["frost"]),
        ("冷火", PALETTE["coldfire"]),
    ]
    lx = 130
    for name, col in legend:
        d.rectangle([lx, fy - 2, lx + 26, fy + 24], fill=tuple(col))
        d.text((lx + 34, fy - 1), name, font=f_small, fill=bone)
        lx += 34 + d.textlength(name, font=f_small) + 26

    d.text((60, fy + 40),
           "地形：world/ 现有数据（2048 m², 513² 顶点）  ·  "
           "注：本图仅为调色/光照方向验证，生物群系语义将在 Phase 2 重新生成",
           font=f_small, fill=dim)

    map_path = os.path.join(OUT, "ashkar-basin.png")
    canvas.save(map_path)
    print("wrote", map_path, canvas.size)

    make_palette()
    make_color_script()


def make_palette():
    W, H = 1400, 1220
    img = Image.new("RGB", (W, H), PALETTE["ink"])
    d = ImageDraw.Draw(img)
    f_h = ImageFont.truetype(FONT_CN_BOLD, 40)
    f_g = ImageFont.truetype(FONT_CN, 22)
    f_n = ImageFont.truetype(FONT_CN, 20)
    f_m = ImageFont.truetype(FONT_MONO, 17)

    bone = tuple(PALETTE["bone"])
    dim = (150, 145, 134)

    d.text((56, 40), "《鸣沙》色板 · 阿什卡", font=f_h, fill=bone)
    d.text((56, 96), "AW-01  PALETTE  ·  暖沙 / 冷火 双极对比", font=f_g, fill=dim)

    groups = [
        ("地表 TERRAIN", [
            ("沙海高光", "sand_lit"), ("沙海", "sand"), ("沙海阴影", "sand_dark"),
            ("盐壳", "salt"), ("盐壳阴影", "salt_shade"),
        ]),
        ("岩与砾 ROCK", [
            ("玄武岩", "basalt"), ("玄武岩受光", "basalt_lit"),
            ("赤铁", "rust"), ("砾漠", "reg"), ("砾漠阴影", "reg_dark"),
        ]),
        ("天光与异色 SKY & ACCENT", [
            ("霜冠", "frost"), ("尘天", "sky"), ("冷火", "coldfire"),
        ]),
        ("阵营 FACTIONS", [
            ("骨白", "bone"), ("黄铜", "brass"), ("警示", "ember"),
        ]),
    ]

    y = 165
    for title, items in groups:
        d.text((56, y), title, font=f_n, fill=dim)
        x = 56
        for name, key in items:
            col = PALETTE[key]
            d.rectangle([x, y + 30, x + 150, y + 180], fill=tuple(col))
            hexs = "#%02X%02X%02X" % col
            # contrast-aware caption
            lum = 0.2126 * col[0] + 0.7152 * col[1] + 0.0722 * col[2]
            cap = (20, 18, 15) if lum > 140 else (235, 230, 220)
            d.text((x + 10, y + 140), hexs, font=f_m, fill=cap)
            d.text((x, y + 190), name, font=f_n, fill=bone)
            x += 166
        y += 240

    d.text((56, H - 52), "线性空间转换：shader 内 pow(c, 2.2)；两端共用同一份字节，不做二次 sRGB 解码",
           font=f_n, fill=dim)

    p = os.path.join(OUT, "palette.png")
    img.save(p)
    print("wrote", p)


def make_color_script():
    """A 16:9 strip showing the day's light key, as a mood reference."""
    W, H = 1400, 420
    img = Image.new("RGB", (W, H), PALETTE["ink"])
    d = ImageDraw.Draw(img)
    f_h = ImageFont.truetype(FONT_CN_BOLD, 30)
    f_s = ImageFont.truetype(FONT_CN, 18)
    bone = tuple(PALETTE["bone"])
    dim = (150, 145, 134)

    d.text((40, 26), "日光脚本 · 一日四段（40 小时制）", font=f_h, fill=bone)

    phases = [
        ("白昼", (196, 170, 128), (233, 205, 158), "双日高照，阴影短而硬"),
        ("暮尘", (176, 140, 96), (214, 186, 142), "赫斯低垂，长影与光柱"),
        ("霜夜", (38, 46, 58), (70, 92, 104), "两月与银河，冷火自发光"),
        ("晨雾", (170, 168, 158), (206, 203, 193), "贴地雾，盐壳泛白"),
    ]
    x = 40
    for name, c0, c1, note in phases:
        grad = Image.new("RGB", (300, 200))
        gd = ImageDraw.Draw(grad)
        for yy in range(200):
            t = yy / 199
            gd.line([(0, yy), (300, yy)],
                    fill=tuple(int(c0[i] * (1 - t) + c1[i] * t) for i in range(3)))
        img.paste(grad, (x, 96))
        d.text((x, 306), name, font=f_s, fill=bone)
        d.text((x, 330), note, font=f_s, fill=dim)
        x += 330

    p = os.path.join(OUT, "color-script.png")
    img.save(p)
    print("wrote", p)


if __name__ == "__main__":
    main()
