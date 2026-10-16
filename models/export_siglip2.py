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

Two size options, each measured on its own (see models/README.md):

    --text-table int8     the text tower's token table (256,000 x 768, 393 MB
                          of the 565 MB) as int8 with one float16 scale per
                          row; only the 64 gathered rows are dequantised, in
                          the graph. Every other weight stays float16.
    --vision-matmul int8  ONNX Runtime dynamic quantisation of the vision
                          tower's weight MatMuls (int8 weights, activations
                          quantised per call). A new modelId, because image
                          vectors change and stored indexes go stale.

Core ML (macOS) needs three things the plain export lacks, all from
models/coreml_compat.py (model v3):

    explicit zero `pads` on the patch-embedding Conv instead of
    `auto_pad=VALID`, which Core ML refuses;
    --vision-compute fp32 (the default): the vision tower computes in
    float32 from its float16 weights. Core ML runs a float16 graph in
    float16 throughout and lands at cosine 0.983 against the CPU; in
    float32 it lands at 1.00000;
    the input shapes in the manifest, so the library can fix the batch
    dimension by name.

Also written to the output dir:

    tokenizer.model   the Gemma SentencePiece model, copied from the checkpoint
    manifest.json     every file's name, byte size, sha256 and download URL
    verify.json       cosine(ONNX, PyTorch) on ten images and ten sentences
    fixtures/         inputs and expected outputs for the .NET tests

Usage (from the repo root, inside models/.venv):

    python models/export_siglip2.py                 # export + verify
    python models/export_siglip2.py --bench         # also time CPU variants
    python models/export_siglip2.py --images DIR    # verify on real images
    python models/export_siglip2.py --compare       # build and verify every variant

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
RELEASE_TAG = "models-siglip2-base-v3"
MANIFEST_VERSION = 3
# Public, model-only repository: buyers download with no token (owner, 2026-10-16).
REPO = "arbaaz360/siglip2-base-onnx"
OPSET = 17
IMAGE_SIZE = 256
TEXT_LEN = 64
DIMS = 768

HERE = Path(__file__).resolve().parent
OUT = HERE / "out"
SHIPPED_MANIFEST = HERE / "manifest.json"
SHIPPED_FIXTURES = HERE.parent / "search" / "tests" / "Tessera.Desktop.Search.Tests" / "fixtures" / "expected.json"
# Image vectors that agree with the shipped model's at least this well keep
# its modelId: stored indexes stay valid.
SAME_SPACE_COSINE = 0.99999


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


def int8_token_table(src: Path, dst: Path, min_rows: int = 100_000) -> dict:
    """Store the text tower's token embedding table as int8 with one float16
    scale per row (symmetric, scale = max|row| / 127), and dequantise only
    the rows the sentence uses:

        Gather(table) -> X          becomes   Gather(table_q8) -> Cast(float16) --.
                                              Gather(row_scale) ------------------ Mul -> X

    Standard opset-17 ops only, so every execution provider runs it, and
    the 196 MB int8 table is never expanded in memory. Every other weight
    is left as it is in `src` (float16)."""
    import onnx
    from onnx import TensorProto, helper, numpy_helper

    model = onnx.load(str(src))
    g = model.graph
    inits = {i.name: i for i in g.initializer}
    users = {}
    for node in g.node:
        for name in node.input:
            users.setdefault(name, []).append(node)
    tables = [n for n in g.node if n.op_type == "Gather" and n.input[0] in inits
              and inits[n.input[0]].dims[0] >= min_rows]
    if len(tables) != 1:
        raise RuntimeError(f"expected one token-table Gather, found {len(tables)}")
    gather = tables[0]
    table_name = gather.input[0]
    if len(users[table_name]) != 1:
        raise RuntimeError(f"{table_name} has {len(users[table_name])} consumers; expected 1")
    axis = next((a.i for a in gather.attribute if a.name == "axis"), 0)
    if axis != 0:
        raise RuntimeError(f"token-table Gather on axis {axis}")

    init = inits[table_name]
    dtype = init.data_type
    w = numpy_helper.to_array(init).astype(np.float32)
    rows, dims = w.shape
    np_dtype = np.float16 if dtype == TensorProto.FLOAT16 else np.float32
    peak = np.abs(w).max(axis=1, keepdims=True)
    scale = (np.where(peak > 0, peak, 1.0) / 127.0).astype(np_dtype)
    if not (scale > 0).all():
        raise RuntimeError("a row scale underflowed to zero in the stored precision")
    # Round against the stored (float16) scale so q * scale is what the graph computes.
    q = np.clip(np.rint(w / scale.astype(np.float32)), -127, 127).astype(np.int8)
    deq = q.astype(np.float32) * scale.astype(np.float32)
    err = deq - w
    row_cos = (deq * w).sum(1) / np.maximum(
        np.linalg.norm(deq, axis=1) * np.linalg.norm(w, axis=1), 1e-12)

    q_name, s_name = table_name + ".int8", table_name + ".row_scale"
    out = gather.output[0]
    q_out, s_out, c_out = out + "_int8", out + "_scale", out + "_cast"
    new_inits = [i for i in g.initializer if i.name != table_name]
    new_inits += [numpy_helper.from_array(q, q_name), numpy_helper.from_array(scale, s_name)]
    del g.initializer[:]
    g.initializer.extend(new_inits)

    gather.input[0] = q_name
    gather.output[0] = q_out
    extra = [
        helper.make_node("Gather", [s_name, gather.input[1]], [s_out], axis=0,
                         name=gather.name + "_row_scale"),
        helper.make_node("Cast", [q_out], [c_out], to=dtype, name=gather.name + "_dequant_cast"),
        helper.make_node("Mul", [c_out, s_out], [out], name=gather.name + "_dequant_mul"),
    ]
    nodes = list(g.node)
    at = nodes.index(gather) + 1
    del g.node[:]
    g.node.extend(nodes[:at] + extra + nodes[at:])
    onnx.checker.check_model(model)
    onnx.save(model, str(dst))
    return {
        "table": table_name, "rows": int(rows), "dims": int(dims),
        "row_cosine_min": float(row_cos.min()), "row_cosine_mean": float(row_cos.mean()),
        "abs_error_max": float(np.abs(err).max()),
        "rms_error_over_rms_weight": float(np.sqrt((err ** 2).mean() / (w ** 2).mean())),
    }


def int8_matmul_weights(src: Path, dst: Path, min_elements: int = 65_536) -> dict:
    """Weight-only int8: every MatMul whose B is a large float16 initializer
    [K, N] is stored as int8 with one scale per output column and rebuilt in
    the graph (Cast -> Mul -> Cast). Every input is a constant, so ONNX
    Runtime folds the chain into float16 weights when the session loads: the
    download shrinks, the arithmetic is still float16, and activations are
    never quantised. The rebuild is float32 because the CPU provider, which
    does the folding, has no float16 Mul; a float16 Mul would not fold and
    would run on every call."""
    import onnx
    from onnx import TensorProto, helper, numpy_helper

    model = onnx.load(str(src))
    g = model.graph
    inits = {i.name: i for i in g.initializer}
    users = {}
    for node in g.node:
        for name in node.input:
            users.setdefault(name, []).append(node)
    targets = sorted({n.input[1] for n in g.node if n.op_type == "MatMul" and n.input[1] in inits
                      and inits[n.input[1]].data_type == TensorProto.FLOAT16
                      and len(inits[n.input[1]].dims) == 2
                      and int(np.prod(inits[n.input[1]].dims)) >= min_elements
                      and all(u.op_type == "MatMul" and u.input[1] == n.input[1] for u in users[n.input[1]])})
    worst = 1.0
    params = 0
    rebuilt, new_inits = [], []
    for name in targets:
        w = numpy_helper.to_array(inits[name]).astype(np.float32)
        peak = np.abs(w).max(axis=0, keepdims=True)
        scale = (np.where(peak > 0, peak, 1.0) / 127.0).astype(np.float32)
        q = np.clip(np.rint(w / scale), -127, 127).astype(np.int8)
        deq = (q.astype(np.float32) * scale).astype(np.float16).astype(np.float32)
        col_cos = (deq * w).sum(0) / np.maximum(np.linalg.norm(deq, axis=0) * np.linalg.norm(w, axis=0), 1e-12)
        worst = min(worst, float(col_cos.min()))
        params += w.size
        new_inits += [numpy_helper.from_array(q, name + ".int8"), numpy_helper.from_array(scale, name + ".col_scale")]
        rebuilt += [
            helper.make_node("Cast", [name + ".int8"], [name + "_f32"], to=TensorProto.FLOAT,
                             name=name + "_dequant_cast"),
            helper.make_node("Mul", [name + "_f32", name + ".col_scale"], [name + "_scaled"],
                             name=name + "_dequant_mul"),
            helper.make_node("Cast", [name + "_scaled"], [name], to=TensorProto.FLOAT16,
                             name=name + "_dequant_to_fp16"),
        ]
    keep = [i for i in g.initializer if i.name not in set(targets)]
    del g.initializer[:]
    g.initializer.extend(keep + new_inits)
    nodes = list(g.node)
    del g.node[:]
    g.node.extend(rebuilt + nodes)  # weights first, so every MatMul sees B defined
    onnx.checker.check_model(model)
    onnx.save(model, str(dst))
    return {"matmuls": len(targets), "params": params, "column_cosine_min": worst}


def int8_vision_matmuls(src_fp32: Path, dst: Path, per_channel: bool) -> None:
    """ONNX Runtime dynamic quantisation of the weight MatMuls only: int8
    weights stored in the file, activations quantised to uint8 on every
    call (MatMulInteger). Must start from the float32 graph; everything
    that is not a weight MatMul stays float32."""
    from onnxruntime.quantization import QuantType, quantize_dynamic

    quantize_dynamic(
        str(src_fp32), str(dst),
        op_types_to_quantize=["MatMul"],
        weight_type=QuantType.QInt8,
        per_channel=per_channel,
        extra_options={"MatMulConstBOnly": True},
    )


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
    ap.add_argument("--text-table", choices=["fp16", "int8"], default="int8",
                    help="int8 (shipped from v2): the token table as int8 with a float16 scale per row")
    ap.add_argument("--vision-matmul", choices=["fp16", "int8", "int8w"], default="fp16",
                    help="int8: dynamic quantisation of the vision MatMuls; int8w: int8 weights "
                         "rebuilt to float16 in the graph. Either is a new modelId")
    ap.add_argument("--per-channel", action="store_true",
                    help="with --vision-matmul int8: one scale per output column instead of per tensor")
    ap.add_argument("--compare", action="store_true",
                    help="build and verify every variant, whatever is published")
    ap.add_argument("--release", default=RELEASE_TAG, help="release tag the manifest URLs name")
    ap.add_argument("--vision-compute", choices=["fp16", "fp32"], default="fp32",
                    help="fp32 (from v3): the float16 vision graph computes in float32, weights stay "
                         "float16 in the file; Core ML matches the CPU only this way")
    ap.add_argument("--text-from", type=Path, default=None,
                    help="publish this text.onnx instead of the one exported here (verified the same "
                         "way); keeps a released text tower byte for byte")
    ap.add_argument("--model-id", default=None,
                    help="the manifest's modelId; by default the shipped one when the image vectors "
                         f"agree with its fixtures to cosine >= {SAME_SPACE_COSINE}, else the export fails")
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
    sys.path.insert(0, str(HERE))
    from coreml_compat import audit, explicit_conv_pads, float32_islands

    for p in (vision_fp32, text_fp32):
        m = onnx.load(str(p))
        changed = explicit_conv_pads(m)
        if changed:
            onnx.save(m, str(p))
            log(f"{p.name}: " + "; ".join(changed))
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

    # ---- size options, each paired with the other tower unchanged ----------
    # (vision, text) pairs to verify; the base variant's other tower is reused.
    base_v, base_t, _ = variants[args.variant]
    quantised: dict[str, tuple[Path, Path]] = {}
    table_stats = None
    if args.text_table == "int8" or args.compare:
        tq = out / "text.int8table.onnx"
        table_stats = int8_token_table(base_t, tq)
        quantised["int8table"] = (base_v, tq)
        log(f"int8table: text {tq.stat().st_size / 1e6:.1f} MB; table rows cosine min "
            f"{table_stats['row_cosine_min']:.5f}, mean {table_stats['row_cosine_mean']:.5f}")
    if args.vision_matmul == "int8" or args.compare:
        for per_channel in ([False, True] if args.compare else [args.per_channel]):
            suffix = "int8matmul_pc" if per_channel else "int8matmul"
            vq = out / f"vision.{suffix}.onnx"
            int8_vision_matmuls(vision_fp32, vq, per_channel)
            quantised[suffix] = (vq, base_t)
            log(f"{suffix}: vision {vq.stat().st_size / 1e6:.1f} MB")
    weight_stats = None
    if args.vision_matmul == "int8w" or args.compare:
        if args.variant != "fp16":
            raise SystemExit("--vision-matmul int8w starts from the float16 graph (--variant fp16)")
        vw = out / "vision.int8weights.onnx"
        weight_stats = int8_matmul_weights(base_v, vw)
        quantised["int8weights"] = (vw, base_t)
        log(f"int8weights: vision {vw.stat().st_size / 1e6:.1f} MB; {weight_stats['matmuls']} MatMuls, "
            f"column cosine min {weight_stats['column_cosine_min']:.5f}")
    publish_v = base_v
    if args.vision_compute == "fp32":
        if args.variant != "fp16" or args.vision_matmul != "fp16":
            raise SystemExit("--vision-compute fp32 applies to the float16 vision graph (--variant fp16)")
        v32 = out / "vision.fp32compute.onnx"
        m = onnx.load(str(base_v))
        moved = float32_islands(m, lambda n: True)
        onnx.save(m, str(v32))
        quantised["fp32compute"] = (v32, base_t)
        publish_v = v32
        log(f"fp32compute: {moved} vision nodes in float32, weights float16; {v32.stat().st_size / 1e6:.1f} MB")
    if args.vision_matmul == "int8":
        publish_v = quantised["int8matmul_pc" if args.per_channel else "int8matmul"][0]
    elif args.vision_matmul == "int8w":
        publish_v = quantised["int8weights"][0]
    publish_t = quantised["int8table"][1] if args.text_table == "int8" else base_t
    if args.text_from:
        kept = out / "text.kept.onnx"
        shutil.copyfile(args.text_from, kept)
        quantised["kept_text"] = (base_v, kept)
        publish_t = kept
        log(f"text tower: {args.text_from} ({sha256_of(kept)[:16]}…) instead of the export")

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
              "sentences": SENTENCES, "variants": {}, "files": {}}
    if table_stats:
        verify["int8_token_table"] = table_stats
    if weight_stats:
        verify["int8_vision_weights"] = weight_stats
    to_check = {"fp32": (vision_fp32, text_fp32)}
    for name, (vp, tp, _) in variants.items():
        if vp.exists():
            to_check[name] = (vp, tp)
    to_check.update(quantised)
    for vp, tp in to_check.values():
        for p in (vp, tp):
            verify["files"][p.name] = p.stat().st_size
    worst_image: dict[Path, float] = {}
    worst_text: dict[Path, float] = {}
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
        worst_image[vp], worst_text[tp] = float(ci.min()), float(ct.min())
    # The adoption floor for whatever is published, quantised or not (models/README.md).
    assert worst_image[publish_v] >= 0.995, f"{publish_v.name} is below the 0.995 floor"
    assert worst_text[publish_t] >= 0.995, f"{publish_t.name} is below the 0.995 floor"

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
    shutil.copyfile(publish_v, out / "vision.onnx")
    shutil.copyfile(publish_t, out / "text.onnx")
    log(f"published: {publish_v.name} as vision.onnx, {publish_t.name} as text.onnx")
    shutil.copyfile(snap / "tokenizer.model", out / "tokenizer.model")
    findings = {}
    for name in ("vision.onnx", "text.onnx"):
        findings[name] = audit(onnx.load(str(out / name), load_external_data=False))
        for line in findings[name]:
            log(f"coreml audit {name}: {line}")
        implicit = [line for line in findings[name] if "implicit padding" in line or "has auto_pad" in line]
        if implicit:
            raise SystemExit(f"{name}: Core ML refuses implicit padding: {implicit}")
    verify["coreml_audit"] = findings
    (out / "verify.json").write_text(json.dumps(verify, indent=2))
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
    # ---- the shipped model's vectors on the same fixtures -------------------
    # Same pictures, same preprocessing, same runtime: what changes is the
    # graph. Agreement at SAME_SPACE_COSINE keeps the modelId, so stored
    # indexes stay valid; below it the vectors are a new space.
    shipped = json.loads(SHIPPED_MANIFEST.read_text()) if SHIPPED_MANIFEST.exists() else None
    model_id = args.model_id
    if SHIPPED_FIXTURES.exists() and shipped:
        before = json.loads(SHIPPED_FIXTURES.read_text())
        old_img = {f["file"]: np.array(f["embedding"], np.float32) for f in before["images"]}
        old_txt = {t["text"]: np.array(t["embedding"], np.float32) for t in before["texts"]}
        img_cos = {f"{n}.png": float(cosine(e.astype(np.float32), old_img[f"{n}.png"]))
                   for (n, _), e in zip(fixture_images, femb) if f"{n}.png" in old_img}
        txt_cos = [float(cosine(e.astype(np.float32), old_txt[t])) for t, e in zip(fsent, ftxt) if t in old_txt]
        verify["against_shipped"] = {"release": shipped.get("release"), "modelId": shipped["modelId"],
                                     "image_cosines": img_cos, "image_cosine_min": min(img_cos.values()),
                                     "text_cosine_min": min(txt_cos)}
        log(f"against {shipped.get('release')}: image cosine min {min(img_cos.values()):.7f} "
            f"({', '.join(f'{k} {v:.7f}' for k, v in img_cos.items())}); text cosine min {min(txt_cos):.7f}")
        same_space = min(img_cos.values()) >= SAME_SPACE_COSINE
        if model_id is None and args.vision_matmul == "fp16":
            if not same_space:
                raise SystemExit(f"image vectors moved (cosine {min(img_cos.values()):.7f} < {SAME_SPACE_COSINE}) "
                                 f"against {shipped.get('release')}: pass --model-id with a new id")
            model_id = shipped["modelId"]
        (out / "verify.json").write_text(json.dumps(verify, indent=2))
    if model_id is None:
        # Image vectors change only when the vision tower does; a new id makes
        # the core rebuild every stored index instead of mixing two spaces.
        model_id = MODEL_ID + ("-q8" if args.vision_matmul != "fp16" else "")

    (fx / "expected.json").write_text(json.dumps(fixtures))
    log(f"fixtures: {len(fixture_images)} images, {len(fsent)} sentences -> {fx}")

    # ---- manifest ----------------------------------------------------------
    files = []
    for name in ("vision.onnx", "text.onnx", "tokenizer.model"):
        p = out / name
        files.append({
            "name": name, "bytes": p.stat().st_size, "sha256": sha256_of(p),
            "url": f"https://github.com/{REPO}/releases/download/{args.release}/{name}",
        })
    weights = "float16" if args.variant == "fp16" else "float16-weights-float32-compute"
    if args.vision_compute == "fp32":
        weights += "; vision computes in float32"
    if args.text_table == "int8":
        weights += "; text token table int8, per-row scale"
    if args.vision_matmul == "int8":
        weights += "; vision MatMuls int8, dynamic"
    elif args.vision_matmul == "int8w":
        weights += "; vision MatMul weights int8, per-column scale"
    manifest = {
        "modelId": model_id,
        "version": MANIFEST_VERSION,
        "source": HF_MODEL,
        "licence": "Apache-2.0",
        "release": args.release,
        "dimensions": DIMS,
        "imageSize": IMAGE_SIZE,
        "textLength": TEXT_LEN,
        "weights": weights,
        # Shapes as the graphs declare them; the library fixes the free batch
        # dimension by this name for CoreML.
        "inputs": {"vision": {"name": vin.name, "type": vin.type, "shape": vin.shape},
                   "text": {"name": tin.name, "type": tin.type, "shape": tin.shape}},
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
