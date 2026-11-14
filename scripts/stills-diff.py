"""Diff the six biome stills against a reference set.

Usage: python stills-diff.py <reference-dir> <candidate-dir> [--json out.json]

Prints mean/max abs channel delta and the share of pixels that moved > 2, which
separates "same frame, compressed the same" from "the look changed".

Why the numbers and not a pass/fail: at stage 1 the six frames are supposed to be
byte-identical between runs, and -Repeat already enforces that. Against an accepted
BASELINE a difference is expected whenever a look changes on purpose - the reviewer
needs the size of the change, not a red light. So this prints a table and exits 0
even when frames differ; only missing/mismatched-size frames are an error (exit 2).

Render-mode caveat: only compare frames rendered the same way - a batchmode forest
is ~1.5 stops darker than a GUI one inside the vegetation (terrain/sky/fog match).
AshkarStill logs mode=gui|batchmode in its shot= line for this reason.
"""
import json
import sys

import numpy as np
from PIL import Image

FRAMES = ["desert", "ocean", "forest", "steppe", "snow", "mountain"]

if len(sys.argv) < 3:
    print(__doc__)
    sys.exit(2)

ref_dir, cand_dir = sys.argv[1], sys.argv[2]
json_path = ""
if "--json" in sys.argv:
    json_path = sys.argv[sys.argv.index("--json") + 1]

rows = []
errors = 0
print(f"reference: {ref_dir}")
print(f"candidate: {cand_dir}")
print()
print(f"{'frame':9s} {'mean':>8s} {'max':>5s} {'moved>2':>9s}  identical")
for name in FRAMES:
    try:
        a = Image.open(f"{ref_dir}/{name}.png").convert("RGB")
        b = Image.open(f"{cand_dir}/{name}.png").convert("RGB")
    except FileNotFoundError as exc:
        print(f"{name:9s} MISSING ({exc.filename})")
        errors += 1
        continue
    if a.size != b.size:
        print(f"{name:9s} size {a.size} vs {b.size} MISMATCH")
        errors += 1
        continue
    na = np.asarray(a, dtype=np.int16)
    nb = np.asarray(b, dtype=np.int16)
    d = np.abs(na - nb)
    moved = float((d.max(axis=2) > 2).mean()) * 100.0
    identical = not d.any()
    rows.append({
        "frame": name,
        "mean": round(float(d.mean()), 4),
        "max": int(d.max()),
        "moved_pct": round(moved, 3),
        "identical": bool(identical),
    })
    print(f"{name:9s} {d.mean():8.3f} {int(d.max()):5d} {moved:8.3f}%  {'yes' if identical else 'no'}")

if rows:
    changed = [r["frame"] for r in rows if not r["identical"]]
    print()
    print(f"{len(rows) - len(changed)}/{len(rows)} frames identical to the reference"
          + (f"; changed: {', '.join(changed)}" if changed else ""))

if json_path:
    with open(json_path, "w", encoding="utf-8") as fh:
        json.dump({"reference": ref_dir, "candidate": cand_dir, "frames": rows}, fh, indent=2)
    print(f"json: {json_path}")

sys.exit(2 if errors else 0)
