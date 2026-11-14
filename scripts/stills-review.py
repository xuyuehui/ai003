"""Tint and confusability of the six biome stills.

    python stills-review.py [--dir artifacts/ashkar] [--json out.json]

Answers one question per stage: can a stranger tell these frames apart? It prints
per-frame mean colour and saturation, and pairwise 8x8x8 RGB histogram distance
(0 = identical, 2 = no shared colour bucket at all).

Tree counting is NOT done here: a colour rule cannot tell a conifer crown from a
grass-biome slope, and an early version of this file happily reported "45 trees"
in a frame with zero vegetation pixels. Use stills-vegmask.ps1 for vegetation
footprint - it differences the same frame rendered with and without the unlit
vegetation material, which is exact.
"""
import argparse
import json
import os
import sys

import numpy as np
from PIL import Image

FRAMES = ["desert", "ocean", "forest", "steppe", "snow", "mountain"]


def rgb_to_hsv_array(rgb: np.ndarray):
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    mx = rgb.max(axis=2)
    mn = rgb.min(axis=2)
    diff = mx - mn
    sat = np.where(mx > 0, diff / np.maximum(mx, 1e-6), 0.0)
    hue = np.zeros_like(mx)
    nz = diff > 1e-6
    m = nz & (mx == r)
    hue[m] = ((g[m] - b[m]) / diff[m]) % 6.0
    m = nz & (mx == g) & ~(mx == r)
    hue[m] = ((b[m] - r[m]) / diff[m]) + 2.0
    m = nz & (mx == b) & ~(mx == r) & ~(mx == g)
    hue[m] = ((r[m] - g[m]) / diff[m]) + 4.0
    return hue * 60.0, sat, mx


def hist(rgb: np.ndarray, bins: int = 8):
    q = np.clip((rgb * bins).astype(np.int32), 0, bins - 1)
    idx = q[..., 0] * bins * bins + q[..., 1] * bins + q[..., 2]
    counts = np.bincount(idx.ravel(), minlength=bins ** 3).astype(np.float64)
    return counts / counts.sum()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=os.path.join("artifacts", "ashkar"))
    ap.add_argument("--json", default="")
    args = ap.parse_args()

    frames = {}
    for name in FRAMES:
        path = os.path.join(args.dir, name + ".png")
        if not os.path.exists(path):
            print(f"MISSING {path}")
            return 2
        img = Image.open(path).convert("RGB")
        rgb = np.asarray(img, dtype=np.float32) / 255.0
        hue, sat, val = rgb_to_hsv_array(rgb)
        frames[name] = {
            "size": img.size,
            "mean_rgb255": [round(float(c) * 255, 1) for c in rgb.reshape(-1, 3).mean(axis=0)],
            "mean_hue": round(float(hue.mean()), 1),
            "mean_sat": round(float(sat.mean()), 3),
            "mean_val": round(float(val.mean()), 3),
            "hist": hist(rgb),
        }

    print(f"stills dir: {args.dir}")
    print()
    print(f"{'frame':9s} {'size':>10s} {'meanRGB':>18s} {'hue':>6s} {'sat':>6s} {'val':>6s}")
    for name in FRAMES:
        f = frames[name]
        rgb_txt = ",".join(f"{c:.0f}" for c in f["mean_rgb255"])
        print(f"{name:9s} {f['size'][0]:5d}x{f['size'][1]:<4d} {rgb_txt:>18s} "
              f"{f['mean_hue']:6.1f} {f['mean_sat']:6.3f} {f['mean_val']:6.3f}")

    print()
    print("CONFUSION (8x8x8 RGB histogram L1 distance; 0 = identical, 2 = disjoint)")
    distances = {}
    for i, a in enumerate(FRAMES):
        for b in FRAMES[i + 1:]:
            d = float(np.abs(frames[a]["hist"] - frames[b]["hist"]).sum())
            distances[f"{a}|{b}"] = round(d, 4)
            print(f"  {a:9s} vs {b:9s} = {d:.4f}")

    print()
    for name in ("forest", "steppe"):
        others = sorted((float(np.abs(frames[name]["hist"] - frames[o]["hist"]).sum()), o)
                        for o in FRAMES if o != name)
        flag = "FAIL - looks like the closest biome" if others[0][0] < 0.30 else "distinct"
        print(f"  closest to {name:9s}: {others[0][1]:9s} {others[0][0]:.4f}  "
              f"(next {others[1][1]} {others[1][0]:.4f})  -> {flag}")

    if args.json:
        payload = {
            "dir": args.dir,
            "frames": {n: {k: v for k, v in frames[n].items() if k != "hist"} for n in FRAMES},
            "distances": distances,
        }
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump(payload, fh, indent=2)
        print(f"\njson: {args.json}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
