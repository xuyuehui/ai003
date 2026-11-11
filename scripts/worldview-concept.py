#!/usr/bin/env python3
"""Concept frames for the Ashkar worldview (look-dev, not engine renders).

Paints a handful of 2.39:1 key frames from the art-direction doc so the intended
final look can be discussed before any Unity work. Everything is generated from
the palette + the design rules (negative space, silhouettes, two suns, haze).

Usage:
  python scripts/worldview-concept.py            # all scenes + contact sheet
  python scripts/worldview-concept.py S2         # one scene
"""

from __future__ import annotations

import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

W, H = 1440, 604  # 2.385:1
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "docs", "images", "worldview")

FONT_CN = r"C:\Windows\Fonts\msyh.ttc"
FONT_CN_BOLD = r"C:\Windows\Fonts\msyhbd.ttc"
FONT_MONO = r"C:\Windows\Fonts\consola.ttf"


# ------------------------------------------------------------------ utils ---
def hexc(h):
    h = h.lstrip("#")
    return np.array([int(h[i:i + 2], 16) for i in (0, 2, 4)], dtype=np.float64) / 255.0


def lerp(a, b, t):
    return a * (1 - t) + b * t


def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0 + 1e-9), 0, 1)
    return t * t * (3 - 2 * t)


def vgrad(top, bottom, h=H, w=W):
    t = np.linspace(0, 1, h)[:, None, None]
    return np.broadcast_to(lerp(top, bottom, t), (h, w, 3)).copy()


def value_noise(x, seed, n=2048):
    rng = np.random.default_rng(seed)
    vals = rng.normal(size=n + 1)
    xi = np.floor(x).astype(int) % n
    xf = x - np.floor(x)
    a, b = vals[xi], vals[(xi + 1) % n]
    t = xf * xf * (3 - 2 * xf)
    return a * (1 - t) + b * t


def fbm(x, seed, octaves=5, lac=2.0, gain=0.5):
    total, amp, freq, norm = 0.0, 1.0, 1.0, 0.0
    for o in range(octaves):
        total = total + amp * value_noise(x * freq, seed + o * 131)
        norm += amp
        amp *= gain
        freq *= lac
    return total / norm


def gauss_blur(arr, radius):
    a = np.clip(arr, 0, 1)
    mode = "L" if a.ndim == 2 else "RGB"
    img = Image.fromarray((a * 255).astype(np.uint8), mode)
    return np.asarray(img.filter(ImageFilter.GaussianBlur(radius))).astype(np.float64) / 255.0


def glow(img, cx, cy, radius, color, strength=1.0, power=2.6):
    yy, xx = np.mgrid[0:H, 0:W]
    d = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2) / radius
    g = np.exp(-(d ** power)) * strength
    return img + g[..., None] * np.asarray(color)


def sun(img, cx, cy, radius, color, tight=70, wide=280, strength=1.0):
    yy, xx = np.mgrid[0:H, 0:W]
    d = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2)
    disk = (d < radius)[..., None].astype(np.float64)
    img = img * (1 - disk) + np.asarray(color) * disk
    img = glow(img, cx, cy, tight, color, strength * 0.85, 3.2)
    img = glow(img, cx, cy, wide, color, strength * 0.20, 1.7)
    # 水平眩光：太阳压在地平线上时的横向拉伸
    band = np.exp(-((yy - cy) ** 2) / (2 * (radius * 0.55) ** 2)) * np.exp(-((xx - cx) ** 2) / (2 * (wide * 0.85) ** 2))
    img = img + np.asarray(color) * band[..., None] * strength * 0.10
    return img


def stars(img, seed, count=260, ymax=0.62):
    rng = np.random.default_rng(seed)
    xs = rng.integers(0, W, count)
    ys = (rng.random(count) ** 1.6 * H * ymax).astype(int)
    b = rng.random(count) ** 2.2 * 0.85
    for x, y, v in zip(xs, ys, b):
        img[y, x] = np.clip(img[y, x] + np.array([0.75, 0.85, 1.0]) * v, 0, 1)
        if v > 0.55:
            img[max(0, y - 1):y + 2, max(0, x - 1):x + 2] += np.array([0.12, 0.14, 0.18]) * v
    return img


def mask_shape(fn, blur=0.0):
    m = Image.new("L", (W, H), 0)
    fn(ImageDraw.Draw(m))
    if blur:
        m = m.filter(ImageFilter.GaussianBlur(blur))
    return np.asarray(m).astype(np.float64) / 255.0


def composite(img, mask, color, alpha=1.0):
    m = (mask * alpha)[..., None]
    return img * (1 - m) + np.asarray(color) * m


def ridge_curve(base, amp, period, seed, detail=0.22, phase=0.0, sharp=1.0):
    """Dune-like silhouette: sharp crests, rounded troughs, irregular spacing."""
    x = np.arange(W) / W
    rng = np.random.default_rng(seed)
    h = np.zeros(W)
    for f, a, sh in ((1.0, 0.58, 1.6), (1.93, 0.27, 2.3), (3.71, 0.15, 3.0)):
        ph = rng.uniform(0, math.tau)
        s = np.abs(np.sin(x * math.tau * period * f * 0.5 + ph + phase))
        h += a * (1.0 - s) ** sh
    h = h / max(h.max(), 1e-6)
    n = fbm(x * 9.0 + seed * 0.37, seed, 4) * detail * 0.55
    n2 = fbm(x * 30.0 + seed, seed + 7, 3) * detail * 0.16
    return base * H - (h + n + n2) * amp * H


_TEX = None


def sand_texture():
    global _TEX
    if _TEX is None:
        rng = np.random.default_rng(31)
        n = rng.normal(0, 1, (H, W))
        n = gauss_blur(np.clip(0.5 + n * 0.25, 0, 1), 1.2)
        fine = np.asarray(Image.fromarray(
            (np.clip(0.5 + rng.normal(0, 1, (H, W)) * 0.5, 0, 1) * 255).astype(np.uint8)
        ).filter(ImageFilter.GaussianBlur(0.6))).astype(np.float64) / 255.0
        _TEX = (n - 0.5) * 1.6 + (fine - 0.5) * 0.5
    return _TEX


def paint_ground(img, curve, top_color, bottom_color, rim_color=None,
                 rim_strength=0.0, sun_x=0.6, haze=0.0, haze_color=None,
                 texture=0.0, seed=0, shade_left=0.0):
    yy = np.arange(H)[:, None]
    mask = yy > curve[None, :]
    t = np.clip((yy - curve[None, :]) / np.maximum(H - curve[None, :], 1), 0, 1)[..., None]
    col = lerp(np.asarray(top_color), np.asarray(bottom_color), t ** 0.75)

    if texture > 0:
        col = col * (1.0 + sand_texture()[..., None] * texture)

    if shade_left > 0:
        ramp = 1.0 - shade_left * (1.0 - smoothstep(0.0, 0.55, np.arange(W) / W))
        col = col * ramp[None, :, None]

    img = np.where(mask[..., None], col, img)

    if haze > 0:
        img = np.where(mask[..., None], lerp(img, np.asarray(haze_color), haze), img)

    if rim_color is not None and rim_strength > 0:
        edge = mask & ~np.roll(mask, 2, axis=0)
        edge = np.asarray(Image.fromarray((edge * 255).astype(np.uint8))
                          .filter(ImageFilter.GaussianBlur(1.4))).astype(np.float64) / 255.0
        x = np.arange(W) / W
        sunfac = np.exp(-((x - sun_x) ** 2) / 0.055)
        broken = np.clip(0.55 + 0.9 * fbm(x * 7.0 + seed, seed + 3, 3), 0.15, 1.4)
        img = img + edge[..., None] * np.asarray(rim_color) * rim_strength \
            * (sunfac * broken)[None, :, None]
    return img


def dust(img, seed, y0, y1, color, strength=0.35, blobs=26):
    rng = np.random.default_rng(seed)
    layer = np.zeros((H, W))
    im = Image.new("L", (W, H), 0)
    d = ImageDraw.Draw(im)
    for _ in range(blobs):
        cx = rng.uniform(0, W)
        cy = rng.uniform(y0, y1)
        rx = rng.uniform(40, 260)
        ry = rng.uniform(8, 46)
        a = int(rng.uniform(12, 46))
        d.ellipse([cx - rx, cy - ry, cx + rx, cy + ry], fill=a)
    layer = np.asarray(im.filter(ImageFilter.GaussianBlur(26))).astype(np.float64) / 255.0
    return img * (1 - layer[..., None] * strength) + np.asarray(color) * layer[..., None] * strength


def figure(img, cx, ground_y, height, color, seed=0, pose="stand"):
    """Tiny human silhouette; height is in pixels."""
    hh = height
    ww = hh * 0.34
    m = Image.new("L", (W, H), 0)
    d = ImageDraw.Draw(m)
    # 长袍：从肩到下摆一个梯形，下摆微微张开
    d.polygon([(cx - ww * 0.52, ground_y),
               (cx - ww * 0.38, ground_y - hh * 0.74),
               (cx + ww * 0.38, ground_y - hh * 0.74),
               (cx + ww * 0.52, ground_y)], fill=255)
    # 肩
    d.polygon([(cx - ww * 0.55, ground_y - hh * 0.70),
               (cx - ww * 0.30, ground_y - hh * 0.80),
               (cx + ww * 0.30, ground_y - hh * 0.80),
               (cx + ww * 0.55, ground_y - hh * 0.70)], fill=255)
    # 头
    r = hh * 0.082
    d.ellipse([cx - r, ground_y - hh * 0.95, cx + r, ground_y - hh * 0.95 + 2 * r], fill=255)
    m = m.filter(ImageFilter.GaussianBlur(0.7))
    return composite(img, np.asarray(m).astype(np.float64) / 255.0, color)


def post(img, bloom_thresh=1.02, bloom_amt=0.55, grain=1.6, vignette=0.42,
         ca=0.0, contrast=1.0, lift=0.0):
    img = np.clip(img, 0, None)
    bright = np.clip(img - bloom_thresh, 0, None)
    if bright.max() > 0:
        img = img + gauss_blur(bright, 26) * bloom_amt + gauss_blur(bright, 90) * bloom_amt * 0.9

    img = np.clip(img, 0, 1)
    img = (img - 0.5) * contrast + 0.5 + lift

    yy, xx = np.mgrid[0:H, 0:W]
    r = np.sqrt(((xx - W / 2) / (W / 2)) ** 2 + ((yy - H / 2) / (H / 2)) ** 2)
    v = 1 - vignette * np.clip(r - 0.30, 0, None) ** 1.5
    img = img * v[..., None]

    rng = np.random.default_rng(3)
    img = img + rng.normal(0, grain / 255.0, (H, W, 1))

    if ca > 0:
        sh = np.zeros_like(img)
        sh[..., 0] = np.roll(img[..., 0], 1, axis=1) * ca + img[..., 0] * (1 - ca)
        sh[..., 1] = img[..., 1]
        sh[..., 2] = np.roll(img[..., 2], -1, axis=1) * ca + img[..., 2] * (1 - ca)
        img = sh

    return np.clip(img, 0, 1)


# ----------------------------------------------------------------- scenes ---
def scene_s2():
    """长眠海 · 暮尘：双日、沙脊、一个渺小的人。"""
    img = vgrad(hexc("#4C4741"), hexc("#F2C88F"))
    img = lerp(img, hexc("#8A6E55"), np.linspace(0, 1, H)[:, None, None] ** 3 * 0.35)
    img = lerp(img, hexc("#5D5650"), np.linspace(1, 0, H)[:, None, None] ** 3 * 0.35)

    img = sun(img, W * 0.675, H * 0.455, 13, hexc("#FFF0CE"), 56, 260, 1.0)
    img = sun(img, W * 0.185, H * 0.275, 6, hexc("#DC8A5C"), 26, 100, 0.42)

    hz = hexc("#F8DAAA")
    img = dust(img, 11, H * 0.44, H * 0.56, hz, 0.38, 24)

    # 远处骨脊：一道锯齿状的玄武岩剪影，压在地平线后面
    spine = ridge_curve(0.498, 0.062, 3.4, 501, detail=0.85, phase=0.0)
    img = paint_ground(img, spine, hexc("#3E332A"), hexc("#4A3B30"),
                       haze=0.30, haze_color=hz, texture=0.05, seed=501)

    for L in [
        dict(base=0.528, amp=0.030, per=1.6, seed=21, top="#7A6148", bot="#6C553F", haze=0.18, tex=0.08),
        dict(base=0.556, amp=0.040, per=1.2, seed=32, top="#5B4734", bot="#503D2D", haze=0.12, tex=0.09),
        dict(base=0.590, amp=0.048, per=0.9, seed=43, top="#3E2F23", bot="#34271D", haze=0.06, tex=0.10),
    ]:
        c = ridge_curve(L["base"], L["amp"], L["per"], L["seed"], detail=0.34, phase=L["seed"])
        img = paint_ground(img, c, hexc(L["top"]), hexc(L["bot"]),
                           rim_color=hexc("#FFDCA8"), rim_strength=0.0,
                           sun_x=0.675, haze=L["haze"], haze_color=hexc("#D8B487"),
                           texture=L["tex"], seed=L["seed"])

    # 近景沙丘：一条从右下扫到左中的丘脊，人站在脊线上
    x = np.arange(W) / W
    crest = H * (0.945
                 - 0.315 * np.exp(-((x - 0.40) / 0.235) ** 2)
                 - 0.095 * np.exp(-((x - 0.82) / 0.130) ** 2)
                 - 0.050 * np.exp(-((x - 0.07) / 0.085) ** 2))
    crest += fbm(x * 9.0 + 91, 91, 4) * H * 0.016
    img = paint_ground(img, crest, hexc("#7E5937"), hexc("#0A0A0D"),
                       rim_color=hexc("#FFD9A2"), rim_strength=0.85,
                       sun_x=0.675, texture=0.16, seed=91, shade_left=0.32)

    # 沙纹：平行于丘脊的一道道风纹，越靠下越密
    yy = np.arange(H)[:, None]
    near = yy > crest[None, :]
    d = np.maximum(yy - crest[None, :], 0)
    warp = fbm(x * 3.0 + 7, 23, 3)[None, :] * 26.0
    phase = d * 0.78 + warp + d * d * 0.0016
    amp = 0.020 * (0.55 + 0.65 * fbm(x * 8.0, 29, 3))[None, :]
    rip = amp * np.sin(phase) * np.exp(-d / (H * 1.1))
    img = np.where(near[..., None], img * (1 + rip[..., None]), img)

    gx = int(W * 0.402)
    img = figure(img, gx, crest[gx] + 2, 42, hexc("#0C0907"))

    return post(img, bloom_thresh=1.12, bloom_amt=0.40, grain=1.8, vignette=0.45, contrast=1.07)


def scene_elder():
    """长者过境：巨兽贴着沙面推进，人小得几乎看不见。"""
    img = vgrad(hexc("#5A4636"), hexc("#D89A62"))
    img = sun(img, W * 0.72, H * 0.40, 16, hexc("#FFCF98"), 60, 240, 0.85)
    hz = hexc("#E7B47E")
    img = dust(img, 12, H * 0.35, H * 0.55, hz, 0.5, 24)

    for L in [
        dict(base=0.60, amp=0.055, per=1.6, seed=71, top="#B98F63", bot="#9A744E", haze=0.55),
        dict(base=0.66, amp=0.080, per=1.2, seed=82, top="#8E6A48", bot="#6E5036", haze=0.35),
        dict(base=0.74, amp=0.10, per=0.85, seed=93, top="#5C4430", bot="#3E2C20", haze=0.15),
        dict(base=0.83, amp=0.11, per=0.6, seed=104, top="#2C1F17", bot="#160F0C", haze=0.0),
    ]:
        c = ridge_curve(L["base"], L["amp"], L["per"], L["seed"], detail=0.24, phase=L["seed"])
        img = paint_ground(img, c, hexc(L["top"]), hexc(L["bot"]),
                           rim_color=hexc("#FFCE96"), rim_strength=0.45,
                           sun_x=0.72, haze=L["haze"], haze_color=hz)

    # 巨兽：三段拱起的背脊从沙下穿过，像海蛇一样一节一节地露出来
    humps = [(0.18, 0.24, 0.26), (0.47, 0.28, 0.36), (0.76, 0.22, 0.22)]
    rng = np.random.default_rng(21)
    for cx, wdt, hgt in humps:
        us = np.linspace(cx - wdt / 2, cx + wdt / 2, 140)
        uu = (us - (cx - wdt / 2)) / wdt
        ytop = H * (0.82 - hgt * np.sin(np.pi * uu) ** 0.85 * (0.72 + 0.42 * uu))

        body = Image.new("L", (W, H), 0)
        ImageDraw.Draw(body).polygon(list(zip(us * W, ytop)) + [(us[-1] * W, H), (us[0] * W, H)],
                                     fill=255)
        m = np.asarray(body.filter(ImageFilter.GaussianBlur(1.4))).astype(np.float64) / 255.0
        img = composite(img, m, hexc("#0E0A08"), 0.97)

        # 迎光侧的脊线
        rim = Image.new("L", (W, H), 0)
        ImageDraw.Draw(rim).line(list(zip(us * W, ytop)), fill=255, width=2)
        rm = np.asarray(rim.filter(ImageFilter.GaussianBlur(1.2))).astype(np.float64) / 255.0
        img = composite(img, rm, hexc("#C08F62"), 0.55)

        # 环节
        segs = Image.new("L", (W, H), 0)
        sd = ImageDraw.Draw(segs)
        for k in range(5):
            uu2 = 0.14 + k * 0.18
            x = (cx - wdt / 2 + uu2 * wdt) * W
            y = H * (0.82 - hgt * np.sin(np.pi * uu2) ** 0.85)
            sd.line([(x, y + 8), (x + H * 0.10, H * 0.95)], fill=95, width=6)
        sm = np.asarray(segs.filter(ImageFilter.GaussianBlur(3.5))).astype(np.float64) / 255.0
        img = composite(img, sm, hexc("#2A1E16"), 0.5)

        # 背脊晶簇
        for k in range(4):
            uu2 = 0.20 + k * 0.20 + rng.normal(0, 0.02)
            x = (cx - wdt / 2 + uu2 * wdt) * W
            y = H * (0.82 - hgt * np.sin(np.pi * uu2) ** 0.85)
            sh = H * rng.uniform(0.022, 0.052)
            sdw = sh * rng.uniform(0.32, 0.55)
            sp = Image.new("L", (W, H), 0)
            ImageDraw.Draw(sp).polygon([(x - sdw, y + 4), (x + sdw, y + 4), (x, y - sh)], fill=255)
            spm = np.asarray(sp.filter(ImageFilter.GaussianBlur(0.8))).astype(np.float64) / 255.0
            img = composite(img, spm, hexc("#CFE9E4"), 0.85)
            img = glow(img, x, y - sh * 0.8, sh * 0.45, hexc("#8FD9CE"), 0.22, 2.6)

    # 近景沙丘：挡住巨兽的下半身
    c = ridge_curve(0.845, 0.105, 0.62, 104, detail=0.26, phase=104)
    img = paint_ground(img, c, hexc("#4E3826"), hexc("#0B0907"),
                       rim_color=hexc("#FFCE96"), rim_strength=0.55, sun_x=0.72,
                       texture=0.15, seed=104, shade_left=0.3)

    # 沙面被顶起的碎屑
    img = dust(img, 13, H * 0.60, H * 0.80, hexc("#D6AE7E"), 0.26, 20)

    img = figure(img, int(W * 0.605), c[int(W * 0.605)] + 2, 38, hexc("#0C0907"))
    img = figure(img, int(W * 0.645), c[int(W * 0.645)] + 2, 34, hexc("#0C0907"))

    return post(img, bloom_thresh=1.0, bloom_amt=0.6, grain=1.9, vignette=0.5, contrast=1.07)


def scene_s4():
    """霜夜 · 冷火：双月、盐镜、青白晶簇。"""
    horizon = int(H * 0.635)
    img = vgrad(hexc("#050A12"), hexc("#1C2C39"))
    img = stars(img, 5, 380, 0.60)
    img = sun(img, W * 0.255, H * 0.215, 11, hexc("#DCEBF4"), 46, 150, 0.80)
    img = sun(img, W * 0.330, H * 0.115, 6, hexc("#B6CCDA"), 26, 80, 0.45)

    # 远处盐脊
    for L in [
        dict(base=0.585, amp=0.026, per=1.3, seed=201, top="#334555", bot="#22323F", haze=0.35),
        dict(base=0.612, amp=0.034, per=0.9, seed=211, top="#24333F", bot="#16222C", haze=0.18),
    ]:
        c = ridge_curve(L["base"], L["amp"], L["per"], L["seed"], detail=0.18, phase=L["seed"])
        img = paint_ground(img, c, hexc(L["top"]), hexc(L["bot"]),
                           rim_color=hexc("#9FC0D2"), rim_strength=0.30,
                           sun_x=0.255, haze=L["haze"], haze_color=hexc("#22323F"))

    # 冷火晶簇（立在盐镜边上）
    rng = np.random.default_rng(9)
    for i in range(8):
        cx = W * 0.285 + rng.normal(0, 30)
        base_y = horizon - 2 + rng.normal(0, 2)
        hgt = rng.uniform(26, 92)
        wid = hgt * rng.uniform(0.10, 0.19)
        sh = Image.new("L", (W, H), 0)
        ImageDraw.Draw(sh).polygon([(cx - wid, base_y), (cx + wid * 0.7, base_y),
                                    (cx + wid * 0.15, base_y - hgt)], fill=255)
        sm = np.asarray(sh.filter(ImageFilter.GaussianBlur(0.7))).astype(np.float64) / 255.0
        img = composite(img, sm, hexc("#123433"), 0.95)
        core = Image.new("L", (W, H), 0)
        ImageDraw.Draw(core).line([(cx, base_y - 3), (cx + wid * 0.1, base_y - hgt * 0.92)],
                                  fill=255, width=max(1, int(wid * 0.5)))
        cm = np.asarray(core.filter(ImageFilter.GaussianBlur(1.4))).astype(np.float64) / 255.0
        img = composite(img, cm, hexc("#8FE8D6"), 0.85)
        img = glow(img, cx, base_y - hgt * 0.45, hgt * 0.30, hexc("#6CE2C9"), 0.20, 2.4)

    # 盐镜：把地平线以上的画面镜像下来，压暗、模糊、加波纹
    refl = gauss_blur(img[:horizon][::-1], 3.5)
    water = hexc("#101C25")
    refl = lerp(refl, water, 0.45)
    band = img[horizon:]
    hb = band.shape[0]
    ripple = (1.0 + 0.05 * np.sin(np.arange(hb)[:, None] * 0.55))[..., None]
    img[horizon:] = lerp(band, refl[:hb] * ripple, 0.72) * 0.9

    img = figure(img, int(W * 0.66), horizon + 3, 46, hexc("#04070B"))
    return post(img, bloom_thresh=0.95, bloom_amt=0.55, grain=1.5, vignette=0.55, contrast=1.06)


def scene_tide():
    """大潮：一堵墙。"""
    img = vgrad(hexc("#6A4F3C"), hexc("#D6A46E"))
    img = sun(img, W * 0.17, H * 0.315, 12, hexc("#FFD6A0"), 55, 210, 0.80)
    img = dust(img, 14, H * 0.26, H * 0.52, hexc("#D2A06B"), 0.42, 24)

    # 沙暴墙：从右侧压过来，左侧还留着天空
    wall = Image.new("L", (W, H), 0)
    d = ImageDraw.Draw(wall)
    top = []
    for x in range(0, W + 6, 6):
        u = x / W
        rise = smoothstep(0.16, 0.60, u)
        y = H * (0.74 - 0.68 * rise
                 + 0.030 * math.sin(u * 7.0) * rise
                 + 0.040 * fbm(np.array([u * 5.0]), 77, 4)[0] * rise)
        top.append((x, y))
    d.polygon(top + [(W, H), (0, H)], fill=255)
    wm = np.asarray(wall.filter(ImageFilter.GaussianBlur(9))).astype(np.float64) / 255.0
    wall_col = lerp(hexc("#2C1D13"), hexc("#A0754B"),
                    np.linspace(0, 1, H)[:, None, None] ** 0.8)
    img = img * (1 - wm[..., None] * 0.95) + wall_col * wm[..., None] * 0.95

    # 墙内竖向条纹
    streak = 0.60 + 0.40 * fbm(np.arange(W) / W * 26.0, 88, 3)
    img = img * (1 - wm[..., None] * 0.20) + (img * streak[None, :, None]) * wm[..., None] * 0.20

    # 墙面顶缘被太阳照亮
    rim = wm * np.clip(1.0 - np.roll(wm, 5, axis=0), 0, 1)
    rim = gauss_blur(rim, 2.5) * wm
    img = img + rim[..., None] * hexc("#E9B57E") * 0.55

    # 墙脚扬起的沙
    img = dust(img, 16, H * 0.55, H * 0.88, hexc("#C89A68"), 0.42, 26)

    # 前景：井塔与两个人
    tower = Image.new("L", (W, H), 0)
    td = ImageDraw.Draw(tower)
    tx, ty, tw, th = W * 0.14, H * 0.86, 62, 210
    td.polygon([(tx - tw * 0.5, ty - th), (tx + tw * 0.5, ty - th),
                (tx + tw * 0.16, ty), (tx - tw * 0.16, ty)], fill=255)
    td.rectangle([tx - tw * 0.62, ty - th - 8, tx + tw * 0.62, ty - th], fill=255)
    tm = np.asarray(tower.filter(ImageFilter.GaussianBlur(1.0))).astype(np.float64) / 255.0
    img = composite(img, tm, hexc("#241A13"), 0.96)

    img = figure(img, int(W * 0.24), int(H * 0.90), 34, hexc("#1A1310"))
    img = figure(img, int(W * 0.275), int(H * 0.905), 31, hexc("#1A1310"))

    c = ridge_curve(0.90, 0.04, 0.5, 301, detail=0.2, phase=301)
    img = paint_ground(img, c, hexc("#3A2A1E"), hexc("#160F0B"), rim_color=hexc("#C98F5E"),
                       rim_strength=0.25, sun_x=0.38)
    return post(img, bloom_thresh=1.05, bloom_amt=0.45, grain=2.0, vignette=0.5, contrast=1.05)


def scene_s1():
    """第一井 · 清晨：倒锥井塔、钟架、薄雾。"""
    img = vgrad(hexc("#9A9384"), hexc("#E9DCC6"))
    img = sun(img, W * 0.82, H * 0.40, 18, hexc("#FFF4DC"), 70, 280, 0.85)
    img = dust(img, 15, H * 0.45, H * 0.62, hexc("#E6D8C0"), 0.35, 20)

    for L in [
        dict(base=0.60, amp=0.035, per=1.5, seed=401, top="#C0B49C", bot="#A89B84", haze=0.6),
        dict(base=0.66, amp=0.050, per=1.0, seed=411, top="#8C7E68", bot="#6E6250", haze=0.35),
    ]:
        c = ridge_curve(L["base"], L["amp"], L["per"], L["seed"], detail=0.18, phase=L["seed"])
        img = paint_ground(img, c, hexc(L["top"]), hexc(L["bot"]),
                           rim_color=hexc("#FFF0D2"), rim_strength=0.4,
                           sun_x=0.82, haze=L["haze"], haze_color=hexc("#E9DCC6"))

    # 井塔：倒锥 + 顶部平台 + 深窗
    tower = Image.new("L", (W, H), 0)
    td = ImageDraw.Draw(tower)
    tx, ty, tw, th = W * 0.36, H * 0.86, 120, 300
    td.polygon([(tx - tw * 0.5, ty - th), (tx + tw * 0.5, ty - th),
                (tx + tw * 0.14, ty - 10), (tx - tw * 0.14, ty - 10)], fill=255)
    td.rectangle([tx - tw * 0.60, ty - th - 12, tx + tw * 0.60, ty - th], fill=255)
    td.rectangle([tx - 5, ty - th - 34, tx + 5, ty - th - 12], fill=255)
    # 基座：把塔"钉"在地上
    td.rectangle([tx - tw * 0.52, ty - 14, tx + tw * 0.52, ty + 4], fill=255)
    tm = np.asarray(tower.filter(ImageFilter.GaussianBlur(1.0))).astype(np.float64) / 255.0
    img = composite(img, tm, hexc("#2A241D"), 0.95)

    # 塔基的一扇亮门（唯一的人工光源）
    dm = mask_shape(lambda dd: dd.rectangle([tx - 7, ty - 26, tx + 7, ty - 6], fill=255), 1.0)
    img = composite(img, dm, hexc("#FFCE93"), 0.55)
    img = glow(img, tx, ty - 16, 22, hexc("#FFC489"), 0.35, 2.2)

    # 窗
    for i in range(4):
        yy = ty - th * (0.30 + i * 0.14)
        hw = 5 + i * 1.2
        wm = mask_shape(lambda dd, yy=yy, hw=hw, tx=tx: dd.rectangle(
            [tx - hw, yy - 6, tx + hw, yy + 6], fill=255), 1.0)
        img = composite(img, wm, hexc("#FFD9A0"), 0.55)

    # 钟架
    bell = Image.new("L", (W, H), 0)
    bd = ImageDraw.Draw(bell)
    bx, by = W * 0.58, H * 0.86
    bd.line([(bx - 26, by), (bx - 26, by - 70)], fill=255, width=5)
    bd.line([(bx + 26, by), (bx + 26, by - 70)], fill=255, width=5)
    bd.line([(bx - 34, by - 70), (bx + 34, by - 70)], fill=255, width=5)
    bd.polygon([(bx - 13, by - 66), (bx + 13, by - 66), (bx + 9, by - 40), (bx - 9, by - 40)], fill=255)
    bm = np.asarray(bell.filter(ImageFilter.GaussianBlur(0.8))).astype(np.float64) / 255.0
    img = composite(img, bm, hexc("#241D17"), 0.95)

    c = ridge_curve(0.86, 0.045, 0.6, 421, detail=0.2, phase=421)
    img = paint_ground(img, c, hexc("#4A4133"), hexc("#241E17"), rim_color=hexc("#FFEFD0"),
                       rim_strength=0.3, sun_x=0.82)
    img = figure(img, int(W * 0.47), int(H * 0.885), 30, hexc("#1A150F"))
    img = figure(img, int(W * 0.50), int(H * 0.89), 28, hexc("#1A150F"))

    return post(img, bloom_thresh=1.08, bloom_amt=0.4, grain=1.5, vignette=0.35, contrast=1.02)


SCENES = {
    "S1": ("S1 · 第一井的清晨", "WELL ONE · DAWN", scene_s1,
           "建筑尺度 / 薄雾 / 单光源内部"),
    "S2": ("S2 · 长眠海的暮尘", "THE LONG SLEEP · DUST DUSK", scene_s2,
           "双日双影 / 沙脊层次 / 大气透视"),
    "S3": ("S3 · 长者过境", "THE ELDER CROSSING", scene_elder,
           "巨物尺度 / 沙面隆起 / 背脊晶簇"),
    "S4": ("S4 · 霜夜的冷火", "COLDFIRE · FROST NIGHT", scene_s4,
           "双月镜面 / 冷火自发光 / 夜景分级"),
    "S5": ("S5 · 大潮", "THE TIDE", scene_tide,
           "沙暴墙 / 能见度 / 服务器权威事件"),
}


def caption(img_arr, title, en, note):
    im = Image.fromarray((np.clip(img_arr, 0, 1) * 255).astype(np.uint8), "RGB")
    bar = 92
    canvas = Image.new("RGB", (W, H + bar), (14, 12, 10))
    canvas.paste(im, (0, 0))
    d = ImageDraw.Draw(canvas)
    f1 = ImageFont.truetype(FONT_CN_BOLD, 27)
    f2 = ImageFont.truetype(FONT_MONO, 16)
    f3 = ImageFont.truetype(FONT_CN, 17)
    d.text((26, H + 16), title, font=f1, fill=(237, 230, 216))
    d.text((26, H + 54), en, font=f2, fill=(150, 143, 132))
    tw = d.textlength(title, font=f1)
    d.text((26 + tw + 28, H + 26), note, font=f3, fill=(168, 158, 144))
    return canvas


def contact_sheet(paths):
    cols, rows = 3, 2
    pad, cap = 18, 92
    cw, ch = W, H + cap
    sheet = Image.new("RGB", (cols * (cw + pad) + pad, rows * (ch + pad) + pad + 60), (14, 12, 10))
    d = ImageDraw.Draw(sheet)
    f = ImageFont.truetype(FONT_CN_BOLD, 34)
    fs = ImageFont.truetype(FONT_CN, 17)
    d.text((pad + 6, 16), "《鸣沙》关键帧 · 概念参考", font=f, fill=(237, 230, 216))
    d.text((pad + 6, 56), "CONCEPT FRAMES · 2.39:1 · 非引擎渲染，用于确认最终视觉方向", font=fs,
           fill=(150, 143, 132))
    for i, p in enumerate(paths):
        im = Image.open(p)
        im.thumbnail((cw, ch), Image.LANCZOS)
        x = pad + (i % cols) * (cw + pad)
        y = 100 + (i // cols) * (ch + pad)
        sheet.paste(im, (x, y))
    sheet.thumbnail((2400, 2400), Image.LANCZOS)
    out = os.path.join(OUT, "concept", "contact-sheet.jpg")
    sheet.save(out, quality=92, optimize=True)
    print("wrote", out, sheet.size)


def main():
    os.makedirs(os.path.join(OUT, "concept"), exist_ok=True)
    want = sys.argv[1:] or list(SCENES)
    paths = []
    for key in want:
        title, en, fn, note = SCENES[key]
        arr = fn()
        card = caption(arr, title, en, note)
        p = os.path.join(OUT, "concept", f"{key.lower()}.jpg")
        card.save(p, quality=92, optimize=True)
        paths.append(p)
        print("wrote", p, card.size)
    if len(paths) == len(SCENES):
        contact_sheet([os.path.join(OUT, "concept", f"{k.lower()}.jpg") for k in SCENES])


if __name__ == "__main__":
    main()
