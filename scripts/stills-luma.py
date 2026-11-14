"""Compare two renders of the SAME frame, with and without a vegetation mask.

Usage: python stills-luma.py <a.png> <b.png> [--mask mask.png]

Why this exists: "batchmode renders vegetation darker than GUI" needed a number
that settles it, not a screenshot argument. Feed it the two renders and the
vegetation mask produced by stills-vegdiff.py and it reports mean luma inside and
outside the mask for both, plus the changed-pixel share and where those pixels sit.
The interesting pair for the warm-up question is:

    stills-luma.py gui_forest.png warm_batchmode_forest.png --mask forest-mask.png

If the batchmode render still shows inside-mask luma far below the GUI one (the
2026-11-14 measurements were 10.05 vs 24.84) while outside-mask matches to ~0.05,
then the gap is the vegetation material path and GUI stays the appearance source.
If warm-up closes the gap, batchmode is good enough for the six frames.
"""
import argparse
import sys

import numpy as np
from PIL import Image


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("a")
    ap.add_argument("b")
    ap.add_argument("--mask", default="", help="vegetation mask png (255 = vegetation)")
    ap.add_argument("--threshold", type=int, default=2)
    args = ap.parse_args()

    ia = Image.open(args.a).convert("RGB")
    ib = Image.open(args.b).convert("RGB")
    if ia.size != ib.size:
        print(f"SIZE MISMATCH {ia.size} vs {ib.size}")
        return 2

    a = np.asarray(ia, dtype=np.int16)
    b = np.asarray(ib, dtype=np.int16)
    la = a.mean(axis=2)
    lb = b.mean(axis=2)
    delta = np.abs(a - b).max(axis=2)
    changed = delta > args.threshold

    print(f"a: {args.a}")
    print(f"b: {args.b}")
    print(f"frame {ia.size[0]}x{ia.size[1]}  changed(>{args.threshold}) = {changed.mean() * 100:.2f}%")

    if args.mask:
        m = np.asarray(Image.open(args.mask).convert("L")) > 127
        inter = int((changed & m).sum())
        print(f"mask: {args.mask}  covers {m.mean() * 100:.2f}% of frame")
        print(f"  share of changed pixels inside mask = {inter / max(1, changed.sum()) * 100:.2f}%")
        print(f"  inside  mask: luma a={la[m].mean():6.2f}  b={lb[m].mean():6.2f}  gap={la[m].mean() - lb[m].mean():+6.2f}")
        print(f"  outside mask: luma a={la[~m].mean():6.2f}  b={lb[~m].mean():6.2f}  gap={la[~m].mean() - lb[~m].mean():+6.2f}")
        print()
        print("  verdict hint: outside gap ~0 and inside gap large -> vegetation material path;")
        print("                both gaps ~0 -> the two renders agree, modes are interchangeable.")
    else:
        print(f"  whole frame: luma a={la.mean():6.2f}  b={lb.mean():6.2f}  gap={la.mean() - lb.mean():+6.2f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
