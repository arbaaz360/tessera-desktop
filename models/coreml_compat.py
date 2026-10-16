#!/usr/bin/env python
"""
Graph rewrites that make an exported tower acceptable to Core ML, and a
probe that writes the candidates CI tries on a Mac. Needs only onnx and
numpy, so the macOS runner can run it without PyTorch.

    explicit_conv_pads   Conv/pool nodes with implicit padding get explicit
                         zero pads. ONNX Runtime's CoreML builder turns
                         `auto_pad=VALID` into an ML Program conv with
                         pad_type "valid" and no `pad`, which Core ML on
                         macOS 26 refuses ("Required param 'pad' is
                         missing"). Same arithmetic, so identical vectors.
    float32_islands      runs the selected nodes in float32: float16 inputs
                         are cast up where they enter, outputs cast back
                         where they leave, weights stay float16 in the file.
    audit                lists what Core ML could refuse or not take.

    python models/coreml_compat.py audit    <model.onnx>
    python models/coreml_compat.py variants <vision.onnx> <out-root>

`variants` writes <out-root>/<name>/vision.onnx for every candidate in
VARIANTS, each with explicit pads.
"""

from __future__ import annotations

import collections
import sys
from pathlib import Path
from typing import Callable

import onnx
from onnx import NodeProto, TensorProto, helper, shape_inference

# Ops whose padding ONNX Runtime's CoreML builder spells as pad_type + pad.
PADDED_OPS = {"Conv", "ConvTranspose", "MaxPool", "AveragePool"}

# Ops that never compute on float data, or whose output type is their own
# attribute: they stay outside a float32 island and read whatever comes in.
TYPE_AGNOSTIC_READERS = {"Shape", "Size", "Cast"}


def explicit_conv_pads(model: onnx.ModelProto) -> list[str]:
    """`auto_pad` VALID or NOTSET without `pads` -> `pads` of zeros. SAME_*
    padding is left alone (it depends on the input size). Returns what changed."""
    changed = []
    for node in model.graph.node:
        if node.op_type not in PADDED_OPS:
            continue
        attrs = {a.name: a for a in node.attribute}
        auto_pad = attrs["auto_pad"].s.decode() if "auto_pad" in attrs else "NOTSET"
        if auto_pad not in ("VALID", "NOTSET") or ("pads" in attrs and auto_pad == "NOTSET"):
            continue
        spatial = len(helper.get_attribute_value(attrs["kernel_shape"]))
        if "auto_pad" in attrs:
            node.attribute.remove(attrs["auto_pad"])
        if "pads" in attrs:
            node.attribute.remove(attrs["pads"])
        node.attribute.append(helper.make_attribute("pads", [0] * (2 * spatial)))
        changed.append(f"{node.name}: auto_pad {auto_pad} -> pads {[0] * (2 * spatial)}")
    return changed


def _types(model: onnx.ModelProto) -> dict[str, int]:
    inferred = shape_inference.infer_shapes(model, strict_mode=True)
    g = inferred.graph
    types = {v.name: v.type.tensor_type.elem_type for v in [*g.value_info, *g.input, *g.output]}
    types.update({i.name: i.data_type for i in model.graph.initializer})
    return types


def float32_islands(model: onnx.ModelProto, select: Callable[[NodeProto], bool]) -> int:
    """Run every selected node that computes on float16 in float32 instead.

    Inside a run of selected nodes tensors stay float32; a float16 tensor
    entering is cast up once, a float32 tensor leaving to an unselected node
    is cast back down once. Initializers stay float16 in the file behind a
    Cast, which ONNX Runtime folds when the session loads. Returns the number
    of nodes moved to float32."""
    g = model.graph
    types = _types(model)
    fp16 = TensorProto.FLOAT16

    def computes_fp16(n: NodeProto) -> bool:
        return (n.op_type not in TYPE_AGNOSTIC_READERS | {"Constant"}
                and any(types.get(x) == fp16 for x in [*n.input, *n.output] if x))

    # Indices, not id(): protobuf hands out a new wrapper on every access.
    original = list(g.node)
    region = {k for k, n in enumerate(original) if computes_fp16(n) and select(n)}
    outputs = {o.name for o in g.output}
    widened: set[str] = set()          # float16 tensors now produced as float32
    up: dict[str, str] = {}            # float16 tensor -> its float32 cast
    down: dict[str, str] = {}          # widened tensor -> its float16 cast
    nodes: list[NodeProto] = []

    for k, n in enumerate(original):
        if k in region:
            for i, x in enumerate(n.input):
                if x and types.get(x) == fp16 and x not in widened:
                    if x not in up:
                        up[x] = x + "__f32"
                        nodes.append(helper.make_node("Cast", [x], [up[x]], to=TensorProto.FLOAT,
                                                      name=x + "__to_f32"))
                    n.input[i] = up[x]
            for y in n.output:
                if y and types.get(y) == fp16:
                    if y in outputs:
                        raise ValueError(f"{y} is a graph output; cast it explicitly")
                    widened.add(y)
        else:
            for i, x in enumerate(n.input):
                if x in widened and n.op_type not in TYPE_AGNOSTIC_READERS:
                    if x not in down:
                        down[x] = x + "__f16"
                        nodes.append(helper.make_node("Cast", [x], [down[x]], to=fp16,
                                                      name=x + "__to_f16"))
                    n.input[i] = down[x]
        nodes.append(n)

    del g.node[:]
    g.node.extend(nodes)
    del g.value_info[:]                # stale types; ONNX Runtime infers them again
    onnx.checker.check_model(model, full_check=True)
    return len(region)


# ---------------------------------------------------------------------------
# audit
# ---------------------------------------------------------------------------

# What ONNX Runtime's CoreML provider builds as an ML Program (its
# coreml-execution-provider docs and builders/impl, 1.24). Anything else runs
# on the CPU provider between Core ML partitions.
MLPROGRAM_OPS = {
    "Add", "ArgMax", "AveragePool", "BatchNormalization", "Cast", "Clip", "Concat", "Conv",
    "ConvTranspose", "DepthToSpace", "Div", "Erf", "Gather", "Gemm", "Gelu", "GlobalAveragePool",
    "GlobalMaxPool", "GridSample", "GroupNormalization", "InstanceNormalization",
    "LayerNormalization", "LeakyRelu", "MatMul", "MaxPool", "Max", "Mul", "Pow", "PRelu",
    "Reciprocal", "ReduceMax", "ReduceMean", "ReduceSum", "Relu", "Reshape", "Resize", "Round",
    "Shape", "Slice", "Softmax", "Softplus", "Split", "Sigmoid", "Sqrt", "Squeeze", "Sub", "Tanh",
    "Transpose", "Unsqueeze",
}


def audit(model: onnx.ModelProto) -> list[str]:
    """Lines describing what Core ML could refuse (implicit padding, unbounded
    dimensions) and which ops ONNX Runtime's ML Program builder does not take."""
    lines = []
    g = model.graph
    for v in [*g.input, *g.output]:
        dims = [d.dim_param or d.dim_value for d in v.type.tensor_type.shape.dim]
        symbolic = [d for d in dims if isinstance(d, str)]
        if symbolic:
            lines.append(f"{v.name} {dims}: symbolic {symbolic}; Core ML needs them fixed "
                         f"(SessionOptions.AddFreeDimensionOverrideByName)")
    for n in g.node:
        attrs = {a.name: helper.get_attribute_value(a) for a in n.attribute}
        if n.op_type in PADDED_OPS:
            auto_pad = attrs.get("auto_pad", b"NOTSET").decode()
            if auto_pad != "NOTSET" or "pads" not in attrs:
                lines.append(f"{n.name}: {n.op_type} auto_pad {auto_pad}, pads {attrs.get('pads')}: implicit padding")
        elif "auto_pad" in attrs:
            lines.append(f"{n.name}: {n.op_type} has auto_pad")
    counts = collections.Counter(n.op_type for n in g.node if n.op_type != "Constant")
    other = {op: c for op, c in counts.items() if op not in MLPROGRAM_OPS}
    if other:
        lines.append("not built as ML Program (CPU between partitions unless folded at load): "
                     + ", ".join(f"{op} x{c}" for op, c in sorted(other.items())))
    return lines


# ---------------------------------------------------------------------------
# candidates for the CoreML probe
# ---------------------------------------------------------------------------

VARIANTS: dict[str, Callable[[NodeProto], bool] | None] = {
    "pads": None,                                                   # float16 everywhere
    "ln32": lambda n: n.op_type == "LayerNormalization",
    "mlp32": lambda n: "/mlp/" in n.name,
    "attn32": lambda n: "/self_attn/" in n.name or "/head/attention/" in n.name,
    "all32": lambda n: True,
}


def main(argv: list[str]) -> int:
    if len(argv) == 2 and argv[0] == "audit":
        for line in audit(onnx.load(argv[1])) or ["nothing found"]:
            print(line)
        return 0
    if len(argv) == 3 and argv[0] == "variants":
        src, root = Path(argv[1]), Path(argv[2])
        for name, select in VARIANTS.items():
            model = onnx.load(str(src))
            changed = explicit_conv_pads(model)
            moved = float32_islands(model, select) if select else 0
            out = root / name / src.name
            out.parent.mkdir(parents=True, exist_ok=True)
            onnx.save(model, str(out))
            print(f"{name}: {len(changed)} pad rewrite(s), {moved} node(s) in float32 -> {out} "
                  f"({out.stat().st_size / 1e6:.1f} MB)")
        return 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
