# models — SigLIP2 export and manifest

The search library runs `google/siglip2-base-patch16-256` through ONNX
Runtime. This folder holds the script that produces the ONNX files and the
manifest the library trusts. The model files themselves are not in git; they
are a GitHub release.

| File | What |
|---|---|
| `export_siglip2.py` | downloads the checkpoint, exports the two towers, verifies against PyTorch, writes the manifest and the test fixtures |
| `manifest.json` | name, byte size, sha256 and download URL of every model file; embedded into `Tessera.Desktop.Search` as a resource |
| `out/` | the export output (gitignored): `vision.onnx`, `text.onnx`, `tokenizer.model`, `manifest.json`, `verify.json`, `fixtures/` |
| `.venv/` | the Python environment (gitignored) |

## The published files

Release `models-siglip2-base-v1` on `arbaaz360/tessera-desktop`.

| File | Size | What |
|---|---|---|
| `vision.onnx` | 186 MB | `pixel_values [N,3,256,256] float32` → `image_embeds [N,768]`, L2-normalised |
| `text.onnx` | 565 MB | `input_ids [N,64] int64` → `text_embeds [N,768]`, L2-normalised |
| `tokenizer.model` | 4 MB | the Gemma SentencePiece model, copied from the checkpoint |
| `manifest.json` | 2 KB | the same file as `models/manifest.json` |

755 MB in all. The text tower is big because the Gemma vocabulary is 256,000
rows of 768; the embedding table alone is 393 MB in float16. Weights are
float16, inputs and outputs float32, opset 17. Normalisation is inside the
graph, so a consumer never divides by a norm.

The repository is private, so the plain download URLs answer 404 until it
(or the release) is public. See the search README for the developer token.

## Run the export

```bash
python -m venv models/.venv
models/.venv/Scripts/pip install torch --index-url https://download.pytorch.org/whl/cpu
models/.venv/Scripts/pip install transformers optimum onnx onnxruntime onnxconverter-common sentencepiece pillow
models/.venv/Scripts/python models/export_siglip2.py --bench --images <folder of images>
```

`--images` picks ten pictures from a folder for the verification set (any
images; synthetic swatches are used when it is absent). `--bench` also times
the CPU variants. `--variant fp16w` stores float16 weights but computes in
float32 (bigger in RAM, no faster on this CPU, so not shipped).

Then publish and refresh the embedded copy:

```bash
gh release create models-siglip2-base-v1 models/out/vision.onnx models/out/text.onnx models/out/tokenizer.model models/out/manifest.json
cp models/out/manifest.json models/manifest.json
cp models/out/fixtures/* search/tests/Tessera.Desktop.Search.Tests/fixtures/
```

A new export is a new release tag and a new `version` in the manifest; the
library only ever trusts the hashes it was built with.

## Verification (2026-10-16, ten screenshots and ten sentences)

Cosine between the ONNX output and PyTorch float32, worst case over the ten:

| Variant | Image | Text | Vision img/s (CPU, batch 1) | Text ms |
|---|---|---|---|---|
| float32 | 1.00000 | 1.00000 | 7.4 | 46.9 |
| **float16 (shipped)** | 0.99999 | 1.00000 | 10.9 | 39.2 |
| float16 weights, float32 compute | 1.00000 | 1.00000 | 9.5 | 63.1 |

CPU numbers from ONNX Runtime's Python package on the Core Ultra 7 265K; the
.NET numbers are in `search/README.md`.

## Tokenizer facts the library reproduces

| Fact | Value |
|---|---|
| Model | Gemma SentencePiece (BPE, byte fallback), 256,000 pieces |
| BOS | not added |
| EOS | id 1, appended |
| Pad | id 0, on the right |
| Length | 64, content truncated so the EOS still fits |
| Case | kept as typed (the reference tokenizer does not lowercase, despite `do_lower_case` in its config) |

## Licence

The model is Apache-2.0 (Google, `google/siglip2-base-patch16-256`). The
export script's dependencies: PyTorch (BSD-3), transformers and optimum
(Apache-2.0), onnx (Apache-2.0), onnxruntime (MIT), onnxconverter-common
(MIT), sentencepiece (Apache-2.0), Pillow (MIT-CMU).
