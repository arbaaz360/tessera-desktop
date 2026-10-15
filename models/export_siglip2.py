#!/usr/bin/env python
"""
Export google/siglip2-base-patch16-256 to two ONNX files, one per tower.

    vision.onnx   pixel_values [N,3,256,256] float32  ->  image_embeds [N,768], L2-normalised
    text.onnx     input_ids    [N,64]        int64    ->  text_embeds  [N,768], L2-normalised

Weights are stored as float16; the inputs and outputs stay float32 / int64.
Opset 17. The towers are split the way the transformers.js SigLIP export
splits them (one graph per tower, pooled output, nothing shared), but the
export goes through torch.onnx.export on two thin wrapper modules so the
L2 normalisation is inside the graph and the consumer never has to do it.

Also written to the output dir:

    tokenizer.model   the Gemma SentencePiece model, copied from the checkpoint
    manifest.json     every file's name, byte size, sha256 and download URL
    verify.json       cosine(ONNX, PyTorch) on ten images and ten sentences
    fixtures/         inputs and expected outputs for the .NET tests

Usage (from the repo root, inside models/.venv):

    python models/export_siglip2.py                 # export + verify
    python models/export_siglip2.py --bench         # also time CPU variants
    python models/export_siglip2.py --images DIR    # verify on real images

Nothing here is needed at run time; the product downloads the published
files and verifies them against the embedded manifest.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sys
import time
from pathlib import Path

import warnings

import numpy as np
import torch

warnings.filterwarnings("ignore", message="the float32 number")
import torch.nn.functional as F

HF_MODEL = "google/siglip2-base-patch16-256"
MODEL_ID = "siglip2-base-patch16-256"
RELEASE_TAG = "models-siglip2-base-v1"
REPO = "arbaaz360/tessera-desktop"
OPSET = 17
IMAGE_SIZE = 256
TEXT_LEN = 64
DIMS = 768

HERE = Path(__file__).resolve().parent
OUT = HERE / "out"


# --------------------------------------------------------------------------
# Wrappers: one module per tower, normalisation inside the graph
# --------------------------------------------------------------------------


def _pooled(x):
    # transformers 4.x returns a tensor, 5.x may return a ModelOutput
    if hasattr(x, "pooler_output"):
        return x.pooler_output
    if isinstance(x, (tuple, list)):
        return x[0]
    return x


class VisionTower(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, pixel_values):
        feats = _pooled(self.model.get_image_features(pixel_values=pixel_values))
        return F.normalize(feats, p=2, dim=-1)


class TextTower(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, input_ids):
        feats = _pooled(self.model.get_text_features(input_ids=input_ids))
        return F.normalize(feats, p=2, dim=-1)


# --------------------------------------------------------------------------
# Helpers
# --------------------------------------------------------------------------


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def log(msg: str) -> None:
    print(f"[export] {msg}", flush=True)


def export_tower(module: torch.nn.Module, example: torch.Tensor, path: Path,
                 input_name: str, output_name: str, dynamic: dict) -> None:
    """torch.onnx.export, TorchScript path first (it honours opset 17 exactly),
    the dynamo path when that is gone."""
    module.eval()
    kwargs = dict(
        input_names=[input_name],
        output_names=[output_name],
        opset_version=OPSET,
        do_constant_folding=True,
    )
    try:
        torch.onnx.export(module, (example,), str(path), dynamo=False,
                          dynamic_axes=dynamic, **kwargs)
        log(f"exported {path.name} with the TorchScript exporter")
        return
    except Exception as e:  # noqa: BLE001
        log(f"TorchScript exporter failed ({type(e).__name__}: {e}); trying dynamo")
    batch = torch.export.Dim("batch")
    dynamic_shapes = {input_name: {0: batch}}
    prog = torch.onnx.export(module, (example,), dynamo=True,
                             dynamic_shapes=dynamic_shapes, **kwargs)
    prog.optimize()
    prog.save(str(path))
    log(f"exported {path.name} with the dynamo exporter")


def to_fp16_weights(src: Path, dst: Path) -> None:
    """Convert the whole graph to float16, then put float32 Casts at the
    boundary so the graph's inputs and outputs stay float32 (int64 ids are
    untouched). onnxconverter_common's own keep_io_types left a Conv with a
    float32 X and float16 W, so the boundary casts are inserted by hand."""
    import onnx
    from onnx import TensorProto, helper
    from onnxconverter_common import float16

    model = onnx.load(str(src))
    model16 = float16.convert_float_to_float16(
        model, keep_io_types=False, disable_shape_infer=True)
    g = model16.graph
    # The checkpoint casts pixel_values to the weight dtype inside the graph
    # (SiglipVisionEmbeddings); after conversion that Cast still says float32.
    for node in g.node:
        if node.op_type == "Cast":
            for attr in node.attribute:
                if attr.name == "to" and attr.i == TensorProto.FLOAT:
                    attr.i = TensorProto.FLOAT16
    renames = {}
    pre, post = [], []
    for inp in g.input:
        if inp.type.tensor_type.elem_type == TensorProto.FLOAT16:
            inner = inp.name + "_fp16"
            renames[inp.name] = inner
            inp.type.tensor_type.elem_type = TensorProto.FLOAT
            pre.append(helper.make_node("Cast", [inp.name], [inner], to=TensorProto.FLOAT16,
                                        name="cast_in_" + inp.name))
    for node in g.node:
        for i, name in enumerate(node.input):
            if name in renames:
                node.input[i] = renames[name]
    out_renames = {}
    for out in g.output:
        if out.type.tensor_type.elem_type == TensorProto.FLOAT16:
            inner = out.name + "_fp16"
            out_renames[out.name] = inner
            out.type.tensor_type.elem_type = TensorProto.FLOAT
            post.append(helper.make_node("Cast", [inner], [out.name], to=TensorProto.FLOAT,
                                         name="cast_out_" + out.name))
    for node in g.node:
        for i, name in enumerate(node.output):
            if name in out_renames:
                node.output[i] = out_renames[name]
    nodes = list(g.node)
    del g.node[:]
    g.node.extend(pre + nodes + post)
    onnx.checker.check_model(model16)
    onnx.save(model16, str(dst))


def to_fp16_weights_fp32_compute(src: Path, dst: Path, min_elements: int = 1024) -> None:
    """Store large float32 initializers as float16 and Cast them back to float32
    where they are used. The compute graph stays float32; ONNX Runtime folds the
    Casts into float32 initializers when the session loads, so the model runs at
    float32 speed and costs float16 bytes on disk."""
    import onnx
    from onnx import numpy_helper, TensorProto, helper

    model = onnx.load(str(src))
    graph = model.graph
    new_inits, casts = [], []
    for init in graph.initializer:
        if init.data_type == TensorProto.FLOAT:
            arr = numpy_helper.to_array(init)
            if arr.size >= min_elements:
                h = numpy_helper.from_array(arr.astype(np.float16), init.name + "_fp16")
                new_inits.append(h)
                casts.append(helper.make_node("Cast", [h.name], [init.name],
                                              to=TensorProto.FLOAT, name="cast_" + init.name))
                continue
        new_inits.append(init)
    del graph.initializer[:]
    graph.initializer.extend(new_inits)
    # Casts first, so every consumer sees its input defined before use.
    nodes = list(graph.node)
    del graph.node[:]
    graph.node.extend(casts + nodes)
    onnx.save(model, str(dst))


def cosine(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    a = a / np.linalg.norm(a, axis=-1, keepdims=True)
    b = b / np.linalg.norm(b, axis=-1, keepdims=True)
    return (a * b).sum(-1)


def ort_session(path: Path, threads: int = 0):
    import onnxruntime as ort

    so = ort.SessionOptions()
    if threads:
        so.intra_op_num_threads = threads
    return ort.InferenceSession(str(path), so, providers=["CPUExecutionProvider"])


# --------------------------------------------------------------------------
# Inputs for verification
# --------------------------------------------------------------------------

SENTENCES = [
    "a red square",
    "a blue square",
    "a screenshot of a terminal window with code",
    "a file explorer window listing folders",
    "a web browser showing a video",
    "an ink sketch of a horse",
    "a photo of a mountain lake at sunrise",
    "a spreadsheet with numbers",
    "a cat sleeping on a sofa",
    "a city street at night with neon signs",
]


def synthetic_images() -> list[tuple[str, "Image.Image"]]:
    """Deterministic test pictures: solid swatches, gradients, noise, shapes."""
    from PIL import Image, ImageDraw

    rng = np.random.default_rng(1234)
    out = []
    out.append(("red_swatch", Image.new("RGB", (256, 256), (220, 30, 30))))
    out.append(("blue_swatch", Image.new("RGB", (256, 256), (30, 60, 220))))
    out.append(("green_swatch", Image.new("RGB", (256, 256), (30, 180, 60))))
    g = np.zeros((256, 256, 3), np.uint8)
    g[..., 0] = np.linspace(0, 255, 256)[None, :]
    g[..., 1] = np.linspace(0, 255, 256)[:, None]
    g[..., 2] = 128
    out.append(("gradient_256", Image.fromarray(g)))
    noise = rng.integers(0, 256, (384, 512, 3), dtype=np.uint8)
    out.append(("noise_512x384", Image.fromarray(noise)))
    im = Image.new("RGB", (640, 480), (245, 245, 240))
    d = ImageDraw.Draw(im)
    d.ellipse((120, 80, 520, 400), fill=(250, 200, 40), outline=(60, 60, 60), width=6)
    out.append(("yellow_circle_640x480", im))
    im = Image.new("RGB", (300, 500), (20, 20, 20))
    d = ImageDraw.Draw(im)
    for i in range(0, 500, 40):
        d.line((0, i, 300, i), fill=(230, 230, 230), width=4)
    out.append(("stripes_300x500", im))
    im = Image.new("RGB", (256, 256), (255, 255, 255))
    d = ImageDraw.Draw(im)
    d.rectangle((40, 40, 216, 216), fill=(10, 10, 10))
    out.append(("black_square_on_white", im))
    chk = ((np.indices((256, 256)).sum(0) // 32) % 2 * 255).astype(np.uint8)
    out.append(("checkerboard", Image.fromarray(np.stack([chk] * 3, -1))))
    im = Image.new("RGB", (1024, 256), (90, 150, 220))
    d = ImageDraw.Draw(im)
    d.polygon([(100, 230), (400, 30), (700, 230)], fill=(40, 120, 60))
    out.append(("triangle_1024x256", im))
    return out


def load_real_images(folder: Path, n: int = 10) -> list[tuple[str, "Image.Image"]]:
    from PIL import Image

    files = sorted(p for p in folder.rglob("*")
                   if p.suffix.lower() in {".png", ".jpg", ".jpeg", ".webp"})
    if not files:
        return []
    step = max(1, len(files) // n)
    picked = files[::step][:n]
    return [(p.stem, Image.open(p).convert("RGB")) for p in picked]


# --------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", type=Path, default=OUT)
    ap.add_argument("--images", type=Path, default=None,
                    help="folder of real images for the verification set (optional)")
    ap.add_argument("--bench", action="store_true", help="time the CPU variants")
    ap.add_argument("--keep-fp32", action="store_true", help="keep the fp32 graphs in out/")
    ap.add_argument("--reuse-fp32", action="store_true",
                    help="skip the torch export when out/*.fp32.onnx already exist")
    ap.add_argument("--variant", choices=["fp16", "fp16w"], default="fp16",
                    help="fp16: float16 compute graph; fp16w: float16 weights, float32 compute")
    args = ap.parse_args()

    out: Path = args.out
    out.mkdir(parents=True, exist_ok=True)
    (out / "fixtures").mkdir(exist_ok=True)
    torch.manual_seed(0)
    torch.set_num_threads(max(1, os.cpu_count() // 2))

    from transformers import AutoModel, AutoProcessor
    from huggingface_hub import snapshot_download

    log(f"downloading {HF_MODEL}")
    snap = Path(snapshot_download(HF_MODEL, allow_patterns=[
        "*.json", "*.safetensors", "tokenizer.model", "*.txt"]))
    log(f"checkpoint at {snap}")

    model = AutoModel.from_pretrained(HF_MODEL, torch_dtype=torch.float32,
                                      attn_implementation="eager").eval()
    processor = AutoProcessor.from_pretrained(HF_MODEL)
    tok = processor.tokenizer
    log(f"model {type(model).__name__}, tokenizer {type(tok).__name__}, vocab {len(tok)}")

    # ---- tokenizer facts the .NET side must reproduce ---------------------
    probe = tok("a red square", padding="max_length", max_length=TEXT_LEN,
                truncation=True, return_tensors="np")["input_ids"][0].tolist()
    raw = tok("a red square", add_special_tokens=False)["input_ids"]
    add_bos = probe[0] == tok.bos_token_id and (not raw or raw[0] != tok.bos_token_id)
    add_eos = tok.eos_token_id in probe[:len(raw) + 2] and (not raw or raw[-1] != tok.eos_token_id)
    pad_id = tok.pad_token_id
    pad_side = tok.padding_side
    tokenizer_facts = {
        "type": "sentencepiece",
        "bosId": tok.bos_token_id, "eosId": tok.eos_token_id, "padId": pad_id,
        "unkId": tok.unk_token_id, "addBos": bool(add_bos), "addEos": bool(add_eos),
        "paddingSide": pad_side, "maxLength": TEXT_LEN,
    }
    log(f"tokenizer: {tokenizer_facts}")
    log(f"'a red square' -> {probe[:8]} ... (len {len(probe)})")

    # ---- export ----------------------------------------------------------
    vision_fp32 = out / "vision.fp32.onnx"
    text_fp32 = out / "text.fp32.onnx"
    pv = torch.zeros(1, 3, IMAGE_SIZE, IMAGE_SIZE, dtype=torch.float32)
    ids = torch.full((1, TEXT_LEN), pad_id, dtype=torch.int64)
    if args.reuse_fp32 and vision_fp32.exists() and text_fp32.exists():
        log("reusing existing fp32 exports")
    else:
        with torch.no_grad():
            export_tower(VisionTower(model), pv, vision_fp32, "pixel_values", "image_embeds",
                         {"pixel_values": {0: "batch"}, "image_embeds": {0: "batch"}})
            export_tower(TextTower(model), ids, text_fp32, "input_ids", "text_embeds",
                         {"input_ids": {0: "batch"}, "text_embeds": {0: "batch"}})

    import onnx

    for p in (vision_fp32, text_fp32):
        m = onnx.load(str(p))
        onnx.checker.check_model(m)
        log(f"{p.name}: opset {[o.version for o in m.opset_import if o.domain in ('', 'ai.onnx')]}, "
            f"{p.stat().st_size / 1e6:.1f} MB, {len(m.graph.node)} nodes")

    variants = {
        "fp16": (out / "vision.fp16.onnx", out / "text.fp16.onnx", to_fp16_weights),
        "fp16w": (out / "vision.fp16w.onnx", out / "text.fp16w.onnx", to_fp16_weights_fp32_compute),
    }
    for name, (vp, tp, fn) in variants.items():
        if name != args.variant and not args.bench:
            continue
        fn(vision_fp32, vp)
        fn(text_fp32, tp)
        log(f"{name}: vision {vp.stat().st_size / 1e6:.1f} MB, text {tp.stat().st_size / 1e6:.1f} MB")

    # ---- verification inputs ---------------------------------------------
    images = synthetic_images()
    if args.images:
        real = load_real_images(args.images, 10)
        log(f"{len(real)} real images from {args.images}")
        images = real[:10] if len(real) >= 10 else real + images[: 10 - len(real)]
    images = images[:10]
    pixel = processor.image_processor([im for _, im in images], return_tensors="pt")["pixel_values"]
    assert tuple(pixel.shape[1:]) == (3, IMAGE_SIZE, IMAGE_SIZE), pixel.shape
    text_ids = tok(SENTENCES, padding="max_length", max_length=TEXT_LEN, truncation=True,
                   return_tensors="pt")["input_ids"]
    assert tuple(text_ids.shape) == (len(SENTENCES), TEXT_LEN), text_ids.shape

    with torch.no_grad():
        ref_img = VisionTower(model)(pixel).numpy()
        ref_txt = TextTower(model)(text_ids).numpy()

    # ---- verify every variant against PyTorch ----------------------------
    verify = {"model": HF_MODEL, "opset": OPSET, "images": [n for n, _ in images],
              "sentences": SENTENCES, "variants": {}}
    to_check = {"fp32": (vision_fp32, text_fp32)}
    for name, (vp, tp, _) in variants.items():
        if vp.exists():
            to_check[name] = (vp, tp)
    for name, (vp, tp) in to_check.items():
        sv, st = ort_session(vp), ort_session(tp)
        vin = sv.get_inputs()[0]
        tin = st.get_inputs()[0]
        pv_np = pixel.numpy().astype(np.float16 if "float16" in vin.type else np.float32)
        img = sv.run(None, {vin.name: pv_np})[0].astype(np.float32)
        txt = st.run(None, {tin.name: text_ids.numpy()})[0].astype(np.float32)
        ci = cosine(img, ref_img)
        ct = cosine(txt, ref_txt)
        ni = np.linalg.norm(img, axis=-1)
        nt = np.linalg.norm(txt, axis=-1)
        verify["variants"][name] = {
            "image_cosine_min": float(ci.min()), "image_cosine_mean": float(ci.mean()),
            "text_cosine_min": float(ct.min()), "text_cosine_mean": float(ct.mean()),
            "image_norm_min": float(ni.min()), "image_norm_max": float(ni.max()),
            "text_norm_min": float(nt.min()), "text_norm_max": float(nt.max()),
            "image_cosines": [float(x) for x in ci], "text_cosines": [float(x) for x in ct],
            "input_type": vin.type,
        }
        log(f"{name}: image cosine min {ci.min():.5f} mean {ci.mean():.5f} | "
            f"text cosine min {ct.min():.5f} mean {ct.mean():.5f} | norms "
            f"{ni.min():.4f}-{ni.max():.4f} / {nt.min():.4f}-{nt.max():.4f}")
        if name == args.variant or name == "fp32":
            assert ci.min() >= 0.999 and ct.min() >= 0.999, f"{name} drifted from PyTorch"

    # ---- optional CPU timing of the variants ------------------------------
    if args.bench:
        bench = {}
        for name, (vp, tp) in to_check.items():
            sv, st = ort_session(vp), ort_session(tp)
            vin, tin = sv.get_inputs()[0], st.get_inputs()[0]
            dt = np.float16 if "float16" in vin.type else np.float32
            for batch in (1, 8):
                x = np.repeat(pixel.numpy()[:1], batch, 0).astype(dt)
                sv.run(None, {vin.name: x})
                t0 = time.perf_counter()
                n = 0
                while time.perf_counter() - t0 < 4.0:
                    sv.run(None, {vin.name: x})
                    n += batch
                ips = n / (time.perf_counter() - t0)
                bench[f"{name}_vision_b{batch}_img_per_s"] = round(ips, 2)
                log(f"bench {name} vision batch {batch}: {ips:.2f} img/s")
            ids1 = text_ids.numpy()[:1]
            st.run(None, {tin.name: ids1})
            t0 = time.perf_counter()
            n = 0
            while time.perf_counter() - t0 < 2.0:
                st.run(None, {tin.name: ids1})
                n += 1
            ms = 1000 * (time.perf_counter() - t0) / n
            bench[f"{name}_text_ms"] = round(ms, 2)
            log(f"bench {name} text: {ms:.2f} ms per sentence")
        verify["bench_cpu"] = bench

    (out / "verify.json").write_text(json.dumps(verify, indent=2))

    # ---- choose the published files ---------------------------------------
    vp, tp, _ = variants[args.variant]
    shutil.copyfile(vp, out / "vision.onnx")
    shutil.copyfile(tp, out / "text.onnx")
    shutil.copyfile(snap / "tokenizer.model", out / "tokenizer.model")
    if not args.keep_fp32:
        vision_fp32.unlink(missing_ok=True)
        text_fp32.unlink(missing_ok=True)

    # ---- fixtures for the .NET tests ---------------------------------------
    fx = out / "fixtures"
    sv, st = ort_session(out / "vision.onnx"), ort_session(out / "text.onnx")
    vin, tin = sv.get_inputs()[0], st.get_inputs()[0]
    fixture_images = [im for im in synthetic_images()
                      if im[0] in {"red_swatch", "blue_swatch", "gradient_256",
                                   "yellow_circle_640x480", "stripes_300x500"}]
    fpix = processor.image_processor([im for _, im in fixture_images], return_tensors="np")["pixel_values"]
    femb = sv.run(None, {vin.name: fpix.astype(np.float16 if "float16" in vin.type else np.float32)})[0]
    for (name, im), e in zip(fixture_images, femb):
        im.save(fx / f"{name}.png")
    fsent = SENTENCES + ["", "RED", "a photo of a red square.", "Ünïcödé — façade naïve 日本語", "a " * 100]
    fsent = [s for s in fsent if s.strip()]
    fids = tok(fsent, padding="max_length", max_length=TEXT_LEN, truncation=True,
               return_tensors="np")["input_ids"]
    ftxt = st.run(None, {tin.name: fids})[0]
    fixtures = {
        "tokenizer": tokenizer_facts,
        "images": [{"file": f"{n}.png", "embedding": [float(x) for x in e]}
                   for (n, _), e in zip(fixture_images, femb)],
        "texts": [{"text": s, "inputIds": [int(x) for x in i], "embedding": [float(x) for x in e]}
                  for s, i, e in zip(fsent, fids, ftxt)],
    }
    (fx / "expected.json").write_text(json.dumps(fixtures))
    log(f"fixtures: {len(fixture_images)} images, {len(fsent)} sentences -> {fx}")

    # ---- manifest ----------------------------------------------------------
    files = []
    for name in ("vision.onnx", "text.onnx", "tokenizer.model"):
        p = out / name
        files.append({
            "name": name, "bytes": p.stat().st_size, "sha256": sha256_of(p),
            "url": f"https://github.com/{REPO}/releases/download/{RELEASE_TAG}/{name}",
        })
    manifest = {
        "modelId": MODEL_ID,
        "version": 1,
        "source": HF_MODEL,
        "licence": "Apache-2.0",
        "release": RELEASE_TAG,
        "dimensions": DIMS,
        "imageSize": IMAGE_SIZE,
        "textLength": TEXT_LEN,
        "weights": "float16" if args.variant == "fp16" else "float16-weights-float32-compute",
        "inputs": {"vision": {"name": vin.name, "type": vin.type},
                   "text": {"name": tin.name, "type": tin.type}},
        "tokenizer": tokenizer_facts,
        "files": files,
    }
    (out / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    total = sum(f["bytes"] for f in files)
    log(f"manifest written; download size {total / 1e6:.1f} MB")
    for f in files:
        log(f"  {f['name']:16} {f['bytes'] / 1e6:8.1f} MB  {f['sha256'][:16]}…")
    return 0


if __name__ == "__main__":
    sys.exit(main())
