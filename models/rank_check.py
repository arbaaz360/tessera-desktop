#!/usr/bin/env python
r"""
Does a smaller model still rank pictures the way the shipped one does?

Embeds a small library of real photographs (every picture under
C:\Windows\Web except 4K, deduplicated by sha256 as the product does: 32)
plus four colour swatches, runs the nine ground-truth queries of the root
README with their known answers, and compares every model given on the
command line with the baseline (the first one):

    first hit      the top result, whether it (and the top k, where the
                   answer is a group) is right, and whether it is the same
                   picture the baseline puts first
    top-5 overlap  how many of the baseline's top five the model also has
    cosines        each image and query vector against the baseline's

Usage (from the repo root, inside models/.venv):

    python models/rank_check.py \
        --model v1=models/out/v1 \
        --model int8table=models/out/vision.fp16.onnx,models/out/text.int8table.onnx \
        --torch --json models/out/rank_check.json

A model is a folder holding vision.onnx and text.onnx, or "vision,text"
paths. --torch adds the PyTorch float32 model as one more row.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import warnings
from pathlib import Path

import numpy as np

warnings.filterwarnings("ignore")

HF_MODEL = "google/siglip2-base-patch16-256"
TEXT_LEN = 64
WEB = Path(r"C:\Windows\Web")  # every picture except the 4K folder: 34 files, 32 distinct
SWATCHES = {  # 512x512 solid PNGs, as in the end-to-end run of the release app
    "red": (220, 20, 20), "green": (20, 180, 40),
    "blue": (20, 40, 220), "yellow": (240, 220, 20),
}
LAKES = {"ThemeC-img28", "ThemeC-img29", "ThemeC-img30", "ThemeC-img31", "Screen-img102"}
KEYBOARD = {f"touchkeyboard-TouchKeyboardTheme{t}00{i}" for t in ("Dark", "Light") for i in range(4)}
GLOW = {"ThemeA-img22", "ThemeA-img20", "Screen-img101", "ThemeA-img23"}
# The nine ground-truth queries of the README status section, exact strings
# (coordinator, 2026-10-16): (query, right first answers, (k, set the top k
# must lie in) or None). Screen-img105 is a plain blue lock screen.
QUERIES = [
    ("a lake with mountains at sunset", LAKES, (5, LAKES)),
    ("pine trees by calm water", {"ThemeC-img29"}, None),
    ("cherry blossom flowers", {"Spotlight-img50"}, None),
    ("a solid red square", {"swatch-red"}, None),
    ("a solid green square", {"swatch-green"}, None),
    ("a solid blue square", {"swatch-blue", "Screen-img105"}, (2, {"swatch-blue", "Screen-img105"})),
    ("a solid yellow square", {"swatch-yellow"}, None),
    ("folds of silk fabric", KEYBOARD, (4, KEYBOARD)),
    ("a glowing planet in dark space", {"ThemeA-img22"}, (4, GLOW)),
]
# For --extra: no right answers, only agreement with the baseline. The
# export's verification sentences and the search README's screenshot queries.
EXTRA_QUERIES = [
    "a red square", "a blue square", "a screenshot of a terminal window with code",
    "a file explorer window listing folders", "a web browser showing a video",
    "an ink sketch of a horse", "a photo of a mountain lake at sunrise", "a spreadsheet with numbers",
    "a cat sleeping on a sofa", "a city street at night with neon signs",
    "a terminal window with code", "a chat conversation", "an error dialog box",
    "a code editor with a dark theme", "a settings or preferences dialog", "a download or progress bar",
]


def cosine(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    # float16 graphs return vectors a few 1e-4 off unit length; compare directions
    a = a / np.linalg.norm(a, axis=-1, keepdims=True)
    b = b / np.linalg.norm(b, axis=-1, keepdims=True)
    return (a * b).sum(-1)


def library_images():
    from PIL import Image

    seen = {}
    for p in sorted(WEB.rglob("*")):
        if p.suffix.lower() not in {".jpg", ".jpeg", ".png"} or "4K" in p.relative_to(WEB).parts:
            continue
        h = hashlib.sha256(p.read_bytes()).hexdigest()
        seen.setdefault(h, p)  # a file's identity is its hash
    items = [(f"{p.parent.name}-{p.stem}", Image.open(p).convert("RGB")) for p in seen.values()]
    items += [(f"swatch-{name}", Image.new("RGB", (512, 512), rgb)) for name, rgb in SWATCHES.items()]
    return items


def ort_pair(spec: str):
    import onnxruntime as ort

    parts = spec.split(",")
    if len(parts) == 1:
        vision, text = Path(parts[0]) / "vision.onnx", Path(parts[0]) / "text.onnx"
    else:
        vision, text = Path(parts[0]), Path(parts[1])
    so = ort.SessionOptions()
    sv = ort.InferenceSession(str(vision), so, providers=["CPUExecutionProvider"])
    st = ort.InferenceSession(str(text), so, providers=["CPUExecutionProvider"])

    def embed_images(pixels: np.ndarray) -> np.ndarray:
        out = [sv.run(None, {sv.get_inputs()[0].name: pixels[i:i + 8]})[0] for i in range(0, len(pixels), 8)]
        return np.concatenate(out).astype(np.float32)

    def embed_texts(ids: np.ndarray) -> np.ndarray:
        return st.run(None, {st.get_inputs()[0].name: ids})[0].astype(np.float32)

    sizes = {"vision_bytes": vision.stat().st_size, "text_bytes": text.stat().st_size}
    return embed_images, embed_texts, sizes


def torch_pair():
    import torch
    import torch.nn.functional as F
    from transformers import AutoModel

    model = AutoModel.from_pretrained(HF_MODEL, torch_dtype=torch.float32, attn_implementation="eager").eval()

    def pooled(x):
        return x.pooler_output if hasattr(x, "pooler_output") else x

    def embed_images(pixels):
        with torch.no_grad():
            f = pooled(model.get_image_features(pixel_values=torch.from_numpy(pixels)))
            return F.normalize(f, dim=-1).numpy()

    def embed_texts(ids):
        with torch.no_grad():
            f = pooled(model.get_text_features(input_ids=torch.from_numpy(ids)))
            return F.normalize(f, dim=-1).numpy()

    return embed_images, embed_texts, {}


def app_index_check(data_dir: Path, specs: list[str], processor, out_json: Path | None) -> int:
    """Score each model's TEXT tower against the image vectors the app itself
    stored (vectors/<modelId>.bin, int8 rows with one scale each), the way
    the app scores them. Exact for a text-only change: the image side is the
    app's own thumbnails through the app's own preprocessing. Pictures are
    matched by sha256; swatches (512x512 PNGs) by their thumbnail's colour."""
    import sqlite3

    import onnxruntime as ort
    from PIL import Image

    db = sqlite3.connect(f"file:{(data_dir / 'library.db').as_posix()}?mode=ro", uri=True)
    sha_to_id = {bytes(s).hex(): cid for cid, s in db.execute("SELECT content_id, sha256 FROM content")}
    names: dict[int, str] = {}
    for p in sorted(WEB.rglob("*")):
        if p.suffix.lower() in {".jpg", ".jpeg", ".png"} and "4K" not in p.relative_to(WEB).parts:
            cid = sha_to_id.get(hashlib.sha256(p.read_bytes()).hexdigest())
            if cid is not None:
                names.setdefault(cid, f"{p.parent.name}-{p.stem}")
    for cid, sha in db.execute("SELECT content_id, hex(sha256) FROM content "
                               "WHERE mime = 'image/png' AND width = 512 AND height = 512"):
        sha = sha.lower()
        thumb = data_dir / "thumbs" / sha[:2] / sha[2:4] / f"{sha}-thumb.webp"
        rgb = np.asarray(Image.open(thumb).convert("RGB"), np.float32).reshape(-1, 3).mean(0)
        names[cid] = "swatch-" + min(SWATCHES, key=lambda k: float(((rgb - SWATCHES[k]) ** 2).sum()))

    (index_path,) = list((data_dir / "vectors").glob("*.bin"))
    raw = index_path.read_bytes()
    dims, count = np.frombuffer(raw, "<i4", 2, 8)
    ids = np.frombuffer(raw, "<i8", count, 32)
    scales = np.frombuffer(raw, "<f4", count, 32 + 8 * count)
    rows = np.frombuffer(raw, np.int8, count * dims, 32 + 12 * count).reshape(count, dims).astype(np.float32)
    keep = [i for i, cid in enumerate(ids) if int(cid) in names]
    label = [names[int(ids[i])] for i in keep]
    print(f"app index {index_path.name}: {count} vectors; {len(keep)} are the wallpapers and swatches")

    queries = [q for q, *_ in QUERIES]
    tok = processor.tokenizer(queries, padding="max_length", max_length=TEXT_LEN, truncation=True,
                              return_tensors="np")["input_ids"].astype(np.int64)
    report, results = {"index": str(index_path), "pictures": label, "models": {}}, {}
    for spec in specs:
        name, _, where = spec.partition("=")
        text = Path(where.split(",")[-1]) if "," in where else Path(where) / "text.onnx"
        st = ort.InferenceSession(str(text), providers=["CPUExecutionProvider"])
        q = st.run(None, {st.get_inputs()[0].name: tok})[0].astype(np.float32)
        q /= np.linalg.norm(q, axis=1, keepdims=True)  # the index normalises the query
        results[name] = (q @ rows[keep].T) * scales[keep]  # dot with the int8 row, times its scale
    base = specs[0].partition("=")[0]
    ok = True
    for name, scores in results.items():
        qrows = []
        for qi, (query, truth, must) in enumerate(QUERIES):
            order = np.argsort(-scores[qi])
            b_order = np.argsort(-results[base][qi])
            top5 = [label[i] for i in order[:5]]
            b_top5 = [label[i] for i in b_order[:5]]
            top_k_right = must is None or {label[i] for i in order[: must[0]]} <= must[1]
            qrows.append({"query": query, "first": top5[0], "firstScore": round(float(scores[qi, order[0]]), 4),
                          "firstMargin": round(float(scores[qi, order[0]] - scores[qi, order[1]]), 4),
                          "firstRight": top5[0] in truth, "topKRight": top_k_right,
                          "sameFirstAsBaseline": top5[0] == b_top5[0], "top5": top5,
                          "top5Scores": [round(float(scores[qi, i]), 4) for i in order[:5]],
                          "top5Overlap": len(set(top5) & set(b_top5))})
        report["models"][name] = {"queries": qrows,
                                  "allRight": all(r["firstRight"] and r["topKRight"] for r in qrows),
                                  "allSameFirst": all(r["sameFirstAsBaseline"] for r in qrows),
                                  "top5OverlapMin": min(r["top5Overlap"] for r in qrows)}
        ok &= report["models"][name]["allRight"] and report["models"][name]["allSameFirst"]
    print(f"| Query | " + " | ".join(results) + " |")
    print("|---|" + "---|" * len(results))
    for qi, (query, *_) in enumerate(QUERIES):
        cells = []
        for name in results:
            r = report["models"][name]["queries"][qi]
            mark = "" if r["firstRight"] and r["topKRight"] else " ✗"
            cells.append(f"{r['first']} {r['firstScore']:.3f}{mark} (margin {r['firstMargin']:.4f}), "
                         f"top-5 {r['top5Overlap']}/5")
        print(f"| {query} | " + " | ".join(cells) + " |")
    for name in results:
        m = report["models"][name]
        print(f"{name:10} right {m['allRight']!s:5}  same first {m['allSameFirst']!s:5}  top-5 overlap min {m['top5OverlapMin']}/5")
    if out_json:
        out_json.write_text(json.dumps(report, indent=2))
    return 0 if ok else 1


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", action="append", required=True, help="name=folder or name=vision,text")
    ap.add_argument("--torch", action="store_true", help="add the PyTorch float32 model")
    ap.add_argument("--extra", type=Path, default=None,
                    help="a folder of more pictures (up to 200) for an agreement-only check "
                         f"with {len(EXTRA_QUERIES)} more queries")
    ap.add_argument("--app-index", type=Path, default=None,
                    help="an app data dir: rank with the image vectors the app stored, text towers only")
    ap.add_argument("--json", type=Path, default=None)
    args = ap.parse_args()
    if args.app_index:
        from transformers import AutoProcessor

        return app_index_check(args.app_index, args.model, AutoProcessor.from_pretrained(HF_MODEL), args.json)

    from PIL import Image
    from transformers import AutoProcessor

    processor = AutoProcessor.from_pretrained(HF_MODEL)
    items = library_images()
    n_truth = len(items)
    queries = [q for q, *_ in QUERIES]
    if args.extra:
        files = sorted(p for p in args.extra.rglob("*") if p.suffix.lower() in {".png", ".jpg", ".jpeg"})
        files = files[:: max(1, len(files) // 200)][:200]
        items += [(p.stem, Image.open(p).convert("RGB")) for p in files]
        queries += EXTRA_QUERIES
    names = [n for n, _ in items]
    pixels = processor.image_processor([im for _, im in items], return_tensors="np")["pixel_values"].astype(np.float32)
    ids = processor.tokenizer(queries, padding="max_length", max_length=TEXT_LEN, truncation=True,
                              return_tensors="np")["input_ids"].astype(np.int64)
    print(f"library: {n_truth} pictures ({n_truth - len(SWATCHES)} distinct photos + "
          f"{len(SWATCHES)} swatches); {len(QUERIES)} queries"
          + (f"; agreement check: {len(items)} pictures, {len(queries)} queries" if args.extra else ""))

    models = []
    for spec in args.model:
        name, _, where = spec.partition("=")
        models.append((name, *ort_pair(where)))
    if args.torch:
        models.append(("pytorch", *torch_pair()))

    vecs = {}
    for name, embed_images, embed_texts, sizes in models:
        vecs[name] = (embed_images(pixels), embed_texts(ids), sizes)

    base = models[0][0]
    b_img, b_txt, _ = vecs[base]
    b_all = b_txt @ b_img.T
    b_scores = b_all[: len(QUERIES), :n_truth]
    report = {"library": names[:n_truth], "queries": queries[: len(QUERIES)], "baseline": base, "models": {}}
    ok = True
    for name, _, _, _ in models:
        img, txt, sizes = vecs[name]
        all_scores = txt @ img.T
        scores = all_scores[: len(QUERIES), :n_truth]
        names_t = names[:n_truth]
        rows = []
        for qi, (q, truth, must) in enumerate(QUERIES):
            order = np.argsort(-scores[qi])
            b_order = np.argsort(-b_scores[qi])
            top5, b_top5 = [names_t[i] for i in order[:5]], [names_t[i] for i in b_order[:5]]
            first = top5[0]
            # Where the answer is a group (five lakes, both blues), the top k must stay in it.
            top_k_right = must is None or {names_t[i] for i in order[: must[0]]} <= must[1]
            rows.append({
                "query": q, "first": first, "firstScore": round(float(scores[qi, order[0]]), 4),
                # How far ahead the first hit is; a near-tie can flip on rounding alone.
                "firstMargin": round(float(scores[qi, order[0]] - scores[qi, order[1]]), 4),
                "firstRight": first in truth, "topKRight": top_k_right,
                "sameFirstAsBaseline": first == b_top5[0],
                "top5": top5, "top5Overlap": len(set(top5) & set(b_top5)),
            })
        img_cos = cosine(img[:n_truth], b_img[:n_truth])
        txt_cos = cosine(txt[: len(QUERIES)], b_txt[: len(QUERIES)])
        m = {
            **sizes,
            "allFirstRight": all(r["firstRight"] and r["topKRight"] for r in rows),
            "allSameFirst": all(r["sameFirstAsBaseline"] for r in rows),
            "top5OverlapMin": min(r["top5Overlap"] for r in rows),
            "top5OverlapMean": round(sum(r["top5Overlap"] for r in rows) / len(rows), 2),
            "imageCosineVsBaselineMin": round(float(img_cos.min()), 5),
            "textCosineVsBaselineMin": round(float(txt_cos.min()), 5),
            "scoreDeltaMax": round(float(np.abs(scores - b_scores).max()), 4),
            "queries": rows,
        }
        if args.extra:
            # Every query over every picture: how often the first hit is the
            # baseline's, and how much of its top five survives.
            same_first, overlap, flips = 0, [], []
            for qi in range(len(queries)):
                o, bo = np.argsort(-all_scores[qi])[:5], np.argsort(-b_all[qi])[:5]
                same_first += int(o[0] == bo[0])
                overlap.append(len(set(o) & set(bo)))
                if o[0] != bo[0]:
                    flips.append({"query": queries[qi], "baseline": names[bo[0]], "model": names[o[0]],
                                  "baselineMargin": round(float(b_all[qi, bo[0]] - b_all[qi, bo[1]]), 4)})
            m["agreement"] = {
                "pictures": len(names), "queries": len(queries),
                "sameFirst": same_first, "top5OverlapMean": round(float(np.mean(overlap)), 2),
                "top5OverlapMin": int(min(overlap)), "firstHitChanges": flips,
                "imageCosineVsBaselineMin": round(float(cosine(img, b_img).min()), 5),
                "textCosineVsBaselineMin": round(float(cosine(txt, b_txt).min()), 5),
            }
        report["models"][name] = m
        ok &= m["allFirstRight"] and m["allSameFirst"]

    print()
    print(f"| Query | " + " | ".join(n for n, *_ in models) + " |")
    print("|---|" + "---|" * len(models))
    for qi, (q, *_) in enumerate(QUERIES):
        cells = []
        for name, *_ in models:
            r = report["models"][name]["queries"][qi]
            mark = "" if r["firstRight"] and r["topKRight"] else " ✗"
            cells.append(f"{r['first']} {r['firstScore']:.3f}{mark}, top-5 {r['top5Overlap']}/5")
        print(f"| {q} | " + " | ".join(cells) + " |")
    print()
    for name, *_ in models:
        m = report["models"][name]
        print(f"{name:14} first right {m['allFirstRight']!s:5}  same first {m['allSameFirst']!s:5}  "
              f"top-5 overlap min {m['top5OverlapMin']}/5 mean {m['top5OverlapMean']}  "
              f"image cos {m['imageCosineVsBaselineMin']:.5f}  text cos {m['textCosineVsBaselineMin']:.5f}  "
              f"max score delta {m['scoreDeltaMax']:.4f}")
    if args.extra:
        print()
        for name, *_ in models:
            a = report["models"][name]["agreement"]
            print(f"{name:14} {a['pictures']} pictures x {a['queries']} queries: same first hit "
                  f"{a['sameFirst']}/{a['queries']}, top-5 overlap mean {a['top5OverlapMean']} min "
                  f"{a['top5OverlapMin']}/5, image cos min {a['imageCosineVsBaselineMin']:.5f}, "
                  f"text cos min {a['textCosineVsBaselineMin']:.5f}")
            for f in a["firstHitChanges"]:
                print(f"{'':16}'{f['query']}': {f['baseline']} -> {f['model']} "
                      f"(baseline margin {f['baselineMargin']:.4f})")
    if args.json:
        args.json.write_text(json.dumps(report, indent=2))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
