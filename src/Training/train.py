"""Trains the killfeed row classifier and exports it for the app.

    python train.py T:/Streams/OBS/Rows labels.json ../Core/Models --player NAME [--holdout AUFNAHME ...]

Rows come from `bf6-highlights export-rows`, reviewed labels from gallery.html. Reviewed rows use
the reviewed label; every other row its rule-based hint, except the own name without anything
after it, whose hint is too often wrong. Whole recordings are held out, and the reported quality
counts only reviewed rows of those recordings. Writes row-classifier.onnx and row-classifier.json.
"""
import argparse
import csv
import json
import random
from pathlib import Path

import cv2
import numpy as np
import torch
from torch import nn

CLASSES = ["kill", "ping", "death", "other"]
HEIGHT, WIDTH = 32, 512


def load_rows(root, reviewed):
    rows = []
    for index in sorted(root.glob("*/rows.csv")):
        recording = index.parent.name
        with index.open(encoding="utf-8", newline="") as handle:
            for row in csv.DictReader(handle):
                key = f"{recording}/rows/{row['file']}"
                if key in reviewed:
                    label, trusted = reviewed[key], True
                elif row["label"] == "kill" and len(row["row_text"].split()) <= 1:
                    continue
                else:
                    label, trusted = row["label"], False
                rows.append({"path": root / key, "recording": recording, "label": CLASSES.index(label),
                             "trusted": trusted})
    return rows


def pixels(path):
    # Same steps as RowClassifier.Classify in the app: OpenCV, RGB, area resampling, 0..1.
    image = cv2.imdecode(np.fromfile(path, dtype=np.uint8), cv2.IMREAD_COLOR)
    image = cv2.resize(cv2.cvtColor(image, cv2.COLOR_BGR2RGB), (WIDTH, HEIGHT), interpolation=cv2.INTER_AREA)
    return image.astype(np.float32).transpose(2, 0, 1) / 255


def augment(batch):
    # Brightness, contrast and a small sideways shift: the killfeed moves with its content.
    scale = torch.empty(batch.shape[0], 1, 1, 1).uniform_(0.8, 1.2)
    offset = torch.empty(batch.shape[0], 1, 1, 1).uniform_(-0.1, 0.1)
    shift = random.randint(-8, 8)
    return torch.roll((batch * scale + offset).clamp(0, 1), shifts=shift, dims=3)


def model():
    def block(inputs, outputs):
        return [nn.Conv2d(inputs, outputs, 3, padding=1), nn.BatchNorm2d(outputs), nn.ReLU(), nn.MaxPool2d(2)]
    # Pooling keeps eight slices across the width: whether the own name sits left or right of the
    # weapon is what tells a kill from a death, and a global average would throw that away.
    return nn.Sequential(*block(3, 16), *block(16, 32), *block(32, 64), *block(64, 64),
                         nn.AdaptiveAvgPool2d((1, 8)), nn.Flatten(), nn.Dropout(0.2),
                         nn.Linear(64 * 8, len(CLASSES)))


def evaluate(net, data, labels):
    net.eval()
    with torch.no_grad():
        probabilities = torch.cat([torch.softmax(net(data[i:i + 256]), 1) for i in range(0, len(data), 256)])
    predicted = probabilities.argmax(1)
    matrix = np.zeros((len(CLASSES), len(CLASSES)), dtype=int)
    for truth, guess in zip(labels.tolist(), predicted.tolist()):
        matrix[truth, guess] += 1
    # The app only vetoes: a row the OCR took for a kill is dropped when the picture shows another
    # class with at least this confidence. Lost kills must stay at zero.
    kill = CLASSES.index("kill")
    confidence = probabilities.max(1).values
    for threshold in (0.8, 0.9, 0.95, 0.99):
        veto = (predicted != kill) & (confidence >= threshold)
        print(f"Veto ab {threshold:.2f}: {int((veto & (labels == kill)).sum())} echte Kills verloren, "
              f"{int((veto & (labels != kill)).sum())} von {int((labels != kill).sum())} Nicht-Kills abgefangen")
    return matrix


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("rows", type=Path)
    parser.add_argument("labels", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--player", nargs="+", required=True,
                        help="Spielernamen der Aufnahmen; die App nutzt das Modell nur für diese")
    parser.add_argument("--holdout", nargs="*")
    parser.add_argument("--epochs", type=int, default=15)
    arguments = parser.parse_args()
    random.seed(7)
    torch.manual_seed(7)

    reviewed = {key: label for key, label in json.loads(arguments.labels.read_text(encoding="utf-8")).items()}
    rows = load_rows(arguments.rows, reviewed)
    recordings = sorted({row["recording"] for row in rows})
    holdout = set(arguments.holdout or recordings[2::5])
    train = [row for row in rows if row["recording"] not in holdout]
    test = [row for row in rows if row["recording"] in holdout and row["trusted"]]
    print(f"Training {len(train)} Zeilen aus {len(recordings) - len(holdout)} Aufnahmen, "
          f"Prüfung {len(test)} geprüfte Zeilen aus {sorted(holdout)}")
    for name, part in (("Training", train), ("Prüfung", test)):
        print(f"  {name}: " + ", ".join(f"{c} {sum(r['label'] == i for r in part)}" for i, c in enumerate(CLASSES)))

    def tensors(part):
        return torch.tensor(np.stack([pixels(row["path"]) for row in part])), \
            torch.tensor([row["label"] for row in part])
    train_x, train_y = tensors(train)
    test_x, test_y = tensors(test) if test else (None, None)

    net = model()
    counts = torch.bincount(train_y, minlength=len(CLASSES)).float().clamp(min=1)
    loss = nn.CrossEntropyLoss(weight=counts.sum() / counts)
    optimizer = torch.optim.Adam(net.parameters(), lr=1e-3)
    for epoch in range(arguments.epochs):
        net.train()
        order = torch.randperm(len(train_x))
        total = 0.0
        for start in range(0, len(order), 64):
            batch = order[start:start + 64]
            optimizer.zero_grad()
            value = loss(net(augment(train_x[batch])), train_y[batch])
            value.backward()
            optimizer.step()
            total += value.item() * len(batch)
        print(f"Epoche {epoch + 1}: Verlust {total / len(order):.4f}")

    if test_x is not None:
        matrix = evaluate(net, test_x, test_y)
        print("Prüfung (Zeilen: echt, Spalten: erkannt):", CLASSES)
        for name, line in zip(CLASSES, matrix):
            print(f"  {name:6} {line.tolist()}")
        kill = CLASSES.index("kill")
        precision = matrix[kill, kill] / max(1, matrix[:, kill].sum())
        recall = matrix[kill, kill] / max(1, matrix[kill].sum())
        print(f"Kill: Präzision {precision:.1%}, Trefferquote {recall:.1%}")

    arguments.output.mkdir(parents=True, exist_ok=True)
    net.eval()
    torch.onnx.export(net, torch.zeros(1, 3, HEIGHT, WIDTH), arguments.output / "row-classifier.onnx",
                      input_names=["row"], output_names=["logits"], opset_version=17, dynamo=False,
                      dynamic_axes={"row": {0: "batch"}, "logits": {0: "batch"}})
    # A ping row carries only the own name. The app test runs it through ONNX Runtime and must get
    # these probabilities back, which pins the preprocessing on both sides.
    reference = next(row for row in rows if row["label"] == CLASSES.index("ping"))
    (arguments.output / "row-classifier-reference.png").write_bytes(reference["path"].read_bytes())
    with torch.no_grad():
        probabilities = torch.softmax(net(torch.tensor(pixels(reference["path"]))[None]), 1)[0].tolist()
    (arguments.output / "row-classifier.json").write_text(json.dumps(
        {"classes": CLASSES, "height": HEIGHT, "width": WIDTH, "scale": "rgb/255",
         "players": arguments.player, "holdout": sorted(holdout), "reference": probabilities},
        indent=2), encoding="utf-8")
    print(arguments.output / "row-classifier.onnx")


if __name__ == "__main__":
    main()
