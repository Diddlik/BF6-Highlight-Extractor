"""Builds gallery.html for reviewing exported killfeed rows by keyboard.

    python make_gallery.py T:/Streams/OBS/Rows [ANZAHL]

Reads every <recording>/rows.csv below the folder written by `bf6-highlights export-rows`, draws
a sample that favours the uncertain rows and writes gallery.html next to them. The page works
offline from the file system; the reviewed labels are downloaded as labels.json.
"""
import csv
import json
import random
import sys
from pathlib import Path

# Uncertain rows first: the own name with nothing after it is a kill with an unreadable victim
# or a ping whose sentence the OCR missed, and only the picture tells which.
QUOTAS = {"name_only": 0.25, "kill": 0.35, "ping": 0.25, "death": 0.15}


def rows(root):
    for index in sorted(root.glob("*/rows.csv")):
        recording = index.parent.name
        with index.open(encoding="utf-8", newline="") as handle:
            for row in csv.DictReader(handle):
                words = row["row_text"].split()
                kind = "name_only" if row["label"] == "kill" and len(words) <= 1 else row["label"]
                yield {"file": f"{recording}/rows/{row['file']}", "recording": recording,
                       "time": float(row["time"]), "hint": row["label"], "text": row["row_text"],
                       "kind": kind}


def sample(all_rows, total, seed=7):
    rng = random.Random(seed)
    picked = []
    for kind, share in QUOTAS.items():
        pool = [row for row in all_rows if row["kind"] == kind]
        rng.shuffle(pool)
        picked += pool[:round(total * share)]
    # A kind with fewer rows than its share leaves room that the other kinds fill.
    chosen = {row["file"] for row in picked}
    rest = [row for row in all_rows if row["file"] not in chosen]
    rng.shuffle(rest)
    picked += rest[:max(0, total - len(picked))]
    rng.shuffle(picked)
    return picked


def main():
    root = Path(sys.argv[1])
    total = int(sys.argv[2]) if len(sys.argv) > 2 else 500
    all_rows = list(rows(root))
    picked = sample(all_rows, total)
    page = (Path(__file__).with_name("gallery.html").read_text(encoding="utf-8")
            .replace("/*ROWS*/[]", json.dumps(picked, ensure_ascii=False)))
    (root / "gallery.html").write_text(page, encoding="utf-8")
    counts = {kind: sum(row["kind"] == kind for row in picked) for kind in QUOTAS}
    print(f"{len(picked)} von {len(all_rows)} Zeilen: {counts}")
    print(root / "gallery.html")


if __name__ == "__main__":
    main()
