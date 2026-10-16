"""Rewrite Conv nodes whose padding is implicit into explicit zero pads.

CI's CoreML probe uses this to test a candidate fix: ONNX Runtime's CoreML
builder turns `auto_pad=VALID` (or no `pads` at all) into an ML Program conv
with pad_type "valid" and no `pad` input, which the Core ML on macOS 26
refuses ("Required param 'pad' is missing"). `pads=[0, 0, 0, 0]` means the
same thing and makes the builder emit pad_type "custom" with `pad`.

    python explicit_pads.py <in.onnx> <out.onnx>
"""

import sys

import onnx
from onnx import helper


def main(src: str, dst: str) -> None:
    model = onnx.load(src)
    changed = 0
    for node in model.graph.node:
        if node.op_type != "Conv":
            continue
        attrs = {a.name: a for a in node.attribute}
        auto_pad = attrs["auto_pad"].s.decode() if "auto_pad" in attrs else "NOTSET"
        if "pads" in attrs or auto_pad not in ("VALID", "NOTSET"):
            continue
        spatial = len(helper.get_attribute_value(attrs["kernel_shape"]))
        if "auto_pad" in attrs:
            node.attribute.remove(attrs["auto_pad"])
        node.attribute.append(helper.make_attribute("pads", [0] * (2 * spatial)))
        changed += 1
        print(f"{node.name}: auto_pad {auto_pad} -> pads {[0] * (2 * spatial)}")
    onnx.checker.check_model(model)
    onnx.save(model, dst)
    print(f"{changed} Conv node(s) rewritten into {dst}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
