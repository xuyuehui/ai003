"""Exact vegetation footprint of a still frame, by differencing two renders.

    python stills-vegdiff.py normal.png unlit.png [--mask out.png]

AshkarStill renders the same frame twice: once with the real vegetation materials
and once with AI003_STILL_UNLIT=1, which swaps only the vegetation material for a
flat unlit colour (verified: the two forest frames differ only inside the canopy
area, max channel delta 4). Everything else in the picture is bit-for-bit the
same, so any pixel above --threshold is vegetation and nothing else - no colour
heuristics, no guessing which brown is terrain.

Prints coverage of the frame, the number of 4-connected vegetation blobs (trees,
lower bound: touching crowns merge) and the largest blob bbox.

Render-mode caveat: this compares two renders made the same way, so it is immune to
the batchmode-vs-GUI gap in vegetation brightness (batchmode URP/Lit trees come out
~1.5 stops darker; terrain/sky/fog are identical). Use it for geometry only - judge
appearance on the GUI artifacts/ashkar/*.png frames, never on a pair from here.
"""
import argparse
import colorsys
import sys

import numpy as np
from PIL import Image


def blobs(mask: np.ndarray, min_area: int):
    work = mask.copy()
    h, w = work.shape
    out = []
    ys, xs = np.nonzero(work)
    for y0_, x0_ in zip(ys.tolist(), xs.tolist()):
        if not work[y0_, x0_]:
            continue
        stack = [(y0_, x0_)]
        work[y0_, x0_] = False
        area = 0
        minx = maxx = x0_
        miny = maxy = y0_
        while stack:
            y, x = stack.pop()
            area += 1
            if x < minx: minx = x
            if x > maxx: maxx = x
            if y < miny: miny = y
            if y > maxy: maxy = y
            if y > 0 and work[y - 1, x]:
                work[y - 1, x] = False
                stack.append((y - 1, x))
            if y + 1 < h and work[y + 1, x]:
                work[y + 1, x] = False
                stack.append((y + 1, x))
            if x > 0 and work[y, x - 1]:
                work[y, x - 1] = False
                stack.append((y, x - 1))
            if x + 1 < w and work[y, x + 1]:
                work[y, x + 1] = False
                stack.append((y, x + 1))
        if area >= min_area:
            out.append((area, (minx, miny, maxx, maxy)))
    out.sort(reverse=True)
    return out


def hue_degrees(rgb: np.ndarray) -> np.ndarray:
    """rgb float 0..1 (n,3) -> hue in degrees."""
    r, g, b = rgb[:, 0], rgb[:, 1], rgb[:, 2]
    mx = rgb.max(axis=1)
    mn = rgb.min(axis=1)
    diff = mx - mn
    hue = np.zeros_like(mx)
    nz = diff > 1e-6
    m = nz & (mx == r)
    hue[m] = ((g[m] - b[m]) / diff[m]) % 6.0
    m = nz & (mx == g) & ~(mx == r)
    hue[m] = ((b[m] - r[m]) / diff[m]) + 2.0
    m = nz & (mx == b) & ~(mx == r) & ~(mx == g)
    hue[m] = ((r[m] - g[m]) / diff[m]) + 4.0
    return hue * 60.0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("normal")
    ap.add_argument("unlit")
    ap.add_argument("--threshold", type=int, default=8,
                    help="channel delta that counts as vegetation (default 8; the "
                         "measured inter-render noise is <=5)")
    ap.add_argument("--min-blob", type=int, default=40)
    ap.add_argument("--separate", type=int, default=4,
                    help="erosion passes used to split touching crowns (0 disables)")
    ap.add_argument("--mask", default="", help="write the mask as a png here")
    ap.add_argument("--criterion", choices=["none", "stage3", "steppe"], default="stage3",
                    help="stage3 = user's option A: top two eighths >=30%%, crowns >=20, "
                         "blobs >=10 (whole-frame 40%% retired); steppe = no tree species")
    args = ap.parse_args()

    a = Image.open(args.normal).convert("RGB")
    b = Image.open(args.unlit).convert("RGB")
    if a.size != b.size:
        print(f"SIZE MISMATCH {a.size} vs {b.size}")
        return 2

    na = np.asarray(a, dtype=np.int16)
    nb = np.asarray(b, dtype=np.int16)
    delta = np.abs(na - nb).max(axis=2)
    mask = delta >= args.threshold
    total = mask.size
    px = int(mask.sum())

    print(f"normal : {args.normal}")
    print(f"unlit  : {args.unlit}")
    print(f"frame  : {a.size[0]}x{a.size[1]}  threshold>={args.threshold}  min-blob={args.min_blob}")
    print(f"delta  : mean={delta.mean():.4f} max={int(delta.max())} "
          f"pixels>=thr={px} ({px / total * 100:.3f}% of frame)")

    bl = blobs(mask, args.min_blob)
    print(f"blobs  : {len(bl)} (>= {args.min_blob} px)")
    for area, box in bl[:10]:
        print(f"         area={area:6d} bbox={box}")
    if len(bl) > 10:
        print(f"         ... {len(bl) - 10} more")
    covered = sum(x[0] for x in bl)
    print(f"coverage: {covered / total * 100:.2f}% of frame in vegetation blobs (>= min-blob)")

    # Touching crowns merge into one blob, so the raw count is a lower bound on
    # trees. Erode the mask a few pixels and count again: crowns separate long
    # before trunks and ground shadow do.
    crowns = 0
    if args.separate > 0:
        work = mask.copy()
        for _ in range(args.separate):
            e = work.copy()
            e[1:, :] &= work[:-1, :]
            e[:-1, :] &= work[1:, :]
            e[:, 1:] &= work[:, :-1]
            e[:, :-1] &= work[:, 1:]
            work = e
            if not work.any():
                break
        sep = blobs(work, max(4, args.min_blob // (args.separate + 1)))
        crowns = len(sep)
        print(f"separated crowns: {len(sep)} after {args.separate} px erosion "
              f"(min area {max(4, args.min_blob // (args.separate + 1))} px) - "
              f"trees whose crowns touch still merge")

    # Stage-3 acceptance (user picked option A on 2026-11-14): the whole-frame
    # >=40% bar is retired - four camera variants topped out at 26-27% on this
    # panorama framing. A conifer forest is now accepted when the TOP TWO EIGHTHS
    # carry >=30% crowns, with >=20 visible crowns and >=10 connected blobs.
    # The steppe criterion is the inverse: vegetation may exist, but none of it
    # may be a tree species.
    if args.criterion == 'stage3':
        h = mask.shape[0]
        top2 = mask[:2 * h // 8]
        top2_pct = float(top2.mean()) * 100.0
        checks = [
            ("top two eighths >= 30%", top2_pct, 30.0, top2_pct >= 30.0, "%"),
            ("visible crowns >= 20", float(crowns), 20.0, crowns >= 20, ""),
            ("canopy blobs >= 10", float(len(bl)), 10.0, len(bl) >= 10, ""),
        ]
        print()
        print("criterion: stage3 (option A, whole-frame 40% retired)")
        for label, value, bar, ok, unit in checks:
            print(f"  [{'PASS' if ok else 'FAIL'}] {label}: {value:.2f}{unit} (bar {bar:g}{unit})")
        overall = all(c[3] for c in checks)
        print(f"  VERDICT: {'PASS' if overall else 'FAIL'}")
        if not overall:
            print("  note: geometry only - appearance is judged on the GUI frames, never here")

    if args.criterion == 'steppe':
        # Reporting only, on purpose. Two attempts at naming species from the unlit
        # colours (hue bins, then chromaticity anchors) gave contradictory answers
        # on frames whose contents are known - tone mapping shifts the flat colours
        # and anti-aliased edges blend into the neighbouring bin. So this prints the
        # dominant vegetation colours for a human to compare against the forest
        # frame instead of pretending to a verdict.
        un = np.asarray(Image.open(args.unlit).convert("RGB"), dtype=np.float32) / 255.0
        sel = un[mask]
        mx = sel.max(axis=1)
        mn = sel.min(axis=1)
        sat = np.where(mx > 0, (mx - mn) / np.maximum(mx, 1e-6), 0.0)
        bright = (sat >= 0.4) & (mx >= 0.15)
        chroma = sel / np.maximum(sel.sum(axis=1, keepdims=True), 1e-6)
        tot = max(1, sel.shape[0])
        print()
        print("steppe report (NOT a verdict): dominant unlit vegetation colours")
        if bright.any():
            q = np.round(chroma[bright] * 20) / 20
            uniq, cnt = np.unique(q, axis=0, return_counts=True)
            order = np.argsort(-cnt)[:4]
            for i in order:
                share = cnt[i] / tot * 100
                print(f"  chromaticity {uniq[i]} : {share:5.1f}% of vegetation")
            print(f"  unclassified (dark/blended): {(~bright).sum() / tot * 100:5.1f}%")
        print("  compare with the forest frame's clusters: a tree species would show up")
        print("  as a colour cluster the steppe does not otherwise contain.")

    if args.mask:
        Image.fromarray((mask * 255).astype(np.uint8)).save(args.mask)
        print(f"mask   : {args.mask}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
