# models — SigLIP2 export and manifest

The search library runs `google/siglip2-base-patch16-256` through ONNX
Runtime. This folder holds the script that produces the ONNX files and the
manifest the library trusts. The model files themselves are not in git; they
are a GitHub release.

| File | What |
|---|---|
| `export_siglip2.py` | downloads the checkpoint, exports the two towers, verifies against PyTorch, writes the manifest and the test fixtures |
| `coreml_compat.py` | what Core ML needs, onnx and numpy only: explicit Conv pads, float32 islands, an audit; `variants` writes the candidates the CI probe tries |
| `rank_check.py` | ranks real photos with nine queries whose answers are known, and compares models with a baseline |
| `manifest.json` | name, byte size, sha256 and download URL of every model file; embedded into `Tessera.Desktop.Search` as a resource |
| `out/` | the export output (gitignored): `vision.onnx`, `text.onnx`, `tokenizer.model`, `manifest.json`, `verify.json`, `fixtures/` |
| `.venv/` | the Python environment (gitignored) |

## The published files

Releases of the public, model-only repository
[`arbaaz360/siglip2-base-onnx`](https://github.com/arbaaz360/siglip2-base-onnx)
(decided by the owner 2026-10-16), so a buyer's app downloads with no token.
Its README carries the Apache-2.0 attribution, the hashes and the list of
changes. `models/manifest.json` (version 4) lists the vision tower once per
platform, each from the release that published it:

| File | Platforms | From release | Size | What |
|---|---|---|---|---|
| `vision.onnx` | Windows, Linux | [`v2`](https://github.com/arbaaz360/siglip2-base-onnx/releases/tag/models-siglip2-base-v2) | 186 MB | `pixel_values [batch,3,256,256] float32` → `image_embeds [batch,768]`, L2-normalised; float16 arithmetic |
| `vision.onnx` | macOS | [`v3`](https://github.com/arbaaz360/siglip2-base-onnx/releases/tag/models-siglip2-base-v3) | 186 MB | the same, float32 arithmetic and explicit Conv pads, which Core ML needs (below) |
| `text.onnx` | all | v3 (byte-identical to v2) | 369 MB | `input_ids [batch,64] int64` → `text_embeds [batch,768]`, L2-normalised; token table int8 |
| `tokenizer.model` | all | v3 (byte-identical to v1, v2) | 4 MB | the Gemma SentencePiece model, copied from the checkpoint |

The library narrows the manifest to its own platform
(`ModelManifest.ForPlatform`), so a machine downloads and verifies three
files. The manifest also records the input shapes, so the library knows
the free dimension is `batch`. The two vision files agree to cosine
0.9999946, so both platforms share one `modelId` and one kind of index.

559 MB per platform, down from 755 MB in v1. Weights are float16 except the text
tower's token table; inputs and outputs float32; opset 17, standard
operators only. Normalisation is inside the graph, so a consumer never
divides by a norm.

`models-siglip2-base-v1` (755 MB, all float16), `-v2` and `-v3` stay
published: builds that shipped with their manifests download them. Every
manifest keeps the `modelId` `siglip2-base-patch16-256`: v2 changed only the
text tower; v3 only the vision tower's arithmetic, and its image vectors
agree with v2's to cosine ≥ 0.9999946 (the five fixtures, and 20 pictures:
ten wallpapers and ten synthetic). An install on Windows upgrading from v2
downloads nothing; a Mac downloads v3's `vision.onnx`.

## Run the export

```bash
python -m venv models/.venv
models/.venv/Scripts/pip install torch --index-url https://download.pytorch.org/whl/cpu
models/.venv/Scripts/pip install transformers optimum onnx onnxruntime onnxconverter-common sentencepiece pillow
models/.venv/Scripts/python models/export_siglip2.py --bench --images <folder of images>
models/.venv/Scripts/python models/rank_check.py --model v1=<folder with the v1 files> --model new=models/out --torch
```

| Option | What |
|---|---|
| `--images DIR` | ten pictures from a folder for the verification set; synthetic swatches when absent |
| `--bench` | times every variant on the CPU |
| `--text-table int8` | the default from v2: the 256,000 × 768 token table as int8, one float16 scale per row, dequantised after the lookup (`fp16` gives v1's text tower) |
| `--vision-matmul int8` | ONNX Runtime dynamic quantisation of the vision MatMuls; fails (below) |
| `--vision-matmul int8w` | int8 weights for the vision MatMuls, rebuilt to float16 when the session loads; passes, not shipped (below) |
| `--compare` | builds and verifies every variant, whatever is published |
| `--reuse-fp32 --keep-fp32` | skip the slow torch export on a second run |
| `--vision-compute fp32` | the default from v3: the vision tower computes in float32 from its float16 weights, the only way Core ML matches the CPU (below). `fp16` gives v2's arithmetic |
| `--text-from FILE` | publish this `text.onnx` instead of the exported one, verified the same way: keeps a released tower byte for byte. Not needed for v3: the export reproduced v2's `text.onnx` exactly |
| `--model-id ID` | the manifest's id when the image vectors moved |
| `--variant fp16w` | float16 weights, float32 compute (bigger in RAM, no faster here, not shipped) |

A vision change that moves image vectors publishes under a new `modelId`
(`…-q8`), because stored indexes would mix two spaces. The export decides:
it compares the new fixture vectors with the shipped ones
(`search/tests/…/fixtures/expected.json`) and keeps the shipped id only at
cosine ≥ 0.99999; below that it stops and asks for `--model-id`.

`rank_check.py` runs the nine ground-truth queries of the root README
status section (exact strings, listed in the script with their accepted
answers) over every picture under `C:\Windows\Web` except 4K (32 distinct)
and four 512×512 swatches, and compares every model with the first one:
first hit, the group answers (five lakes, both blues, the keyboard
textures, the four planets), top-5 overlap, cosines. `--extra DIR` adds up
to 200 more pictures and 16 more queries for an agreement-only check.
`--app-index <data dir>` ranks a text tower against the image vectors the
app itself stored (exact for a text-only change; below).

Then publish and refresh the embedded copy:

```bash
gh release create models-siglip2-base-v3 -R arbaaz360/siglip2-base-onnx models/out/vision.onnx models/out/text.onnx models/out/tokenizer.model models/out/manifest.json
cp models/out/manifest.json models/manifest.json
cp models/out/fixtures/* search/tests/Tessera.Desktop.Search.Tests/fixtures/
```

The version-4 manifest came from the same export with v2's vision file
listed for Windows and Linux; no new release, its entries point at v2's and
v3's assets:

```bash
gh release download models-siglip2-base-v2 -R arbaaz360/siglip2-base-onnx -p vision.onnx -D <dir>
models/.venv/Scripts/python models/export_siglip2.py --reuse-fp32 --keep-fp32 --images <folder> --vision-from <dir>/vision.onnx --vision-from-release models-siglip2-base-v2
```

| Option | What |
|---|---|
| `--vision-from FILE` | list this released `vision.onnx` for `--vision-from-platforms` (default `windows,linux`); the exported one serves the rest. The export checks both give the fixtures' vectors to cosine 0.99999 |
| `--vision-from-release TAG` | the release `--vision-from` was published in; its URL goes in the manifest |

A new export is a new release tag there (`--release`) and a new `version` in
the manifest; the library only ever trusts the hashes it was built with.

## Core ML: model v3 (2026-10-16)

v2 did not run on Core ML through ONNX Runtime's CoreML provider. Measured
on GitHub's `macos-latest` runner (macOS 26, Apple silicon, 3 cores, a
virtual machine), ONNX Runtime 1.24.4, ML Program format:

| Cause | What Core ML said or did | Fix |
|---|---|---|
| the patch-embedding Conv had `auto_pad=VALID` and no `pads` | refused: `Required param 'pad' is missing`. The provider writes `pad_type="valid"` without the `pad` input this Core ML requires | the export writes `pads=[0,0,0,0]` (`coreml_compat.explicit_conv_pads`); same arithmetic |
| the batch dimension `batch` is free | refused: `has unbounded dimension which is not supported` | the library fixes it by name (`AddFreeDimensionOverrideByName`, `search/README.md`); the manifest now records the name |
| float16 arithmetic | loaded, but image vectors at cosine 0.983–0.994 against the CPU provider | the vision tower computes in float32 (`--vision-compute fp32`) |

`coreml_compat.audit` checks every exported graph for the first two (and
lists the ops the provider leaves to the CPU: `Tile`, `Expand`, `Mod`,
`ReduceL2`, `ConstantOfShape` here, between four partitions); the export
stops on implicit padding.

### Why float32 arithmetic

The CPU provider has almost no float16 kernels: it widens float16 weights
to float32 when it loads them and computes in float32, so on the CPU a
float16 graph behaves like a float32 one. Core ML runs an ML Program in the
precision it is typed in. On the runner it ran on its CPU backend every
time (ALL, CPUAndGPU and CPUOnly gave the same vectors to five decimals),
in float16 throughout.

SigLIP2's residual stream carries a few large values from layer 6 on: up to
720 in the last layers (2 to 12 values above 256 per picture, `mean(x²)`
up to 1,290 for a token), next to ordinary values near 1. In float16 the step
between representable numbers near 700 is 0.5. The run that settled it
([38947651278](https://github.com/arbaaz360/tessera-desktop/actions/runs/38947651278))
put each region in float32 in turn (`coreml_compat.float32_islands`, weights
still float16 in the file) and compared Core ML with the CPU on three
swatches and thirteen generated pictures:

| Vision graph on Core ML | Lowest cosine against the CPU | Swatches | Pictures |
|---|---|---|---|
| float16 arithmetic (v2 with explicit pads) | 0.98341 | 0.992–0.994 | 0.983–0.989 |
| LayerNorm in float32 | 0.98338 | the same | the same |
| attention in float32 | 0.98353 | the same | the same |
| MLP in float32 | 0.99523 | 0.9999 | 0.995–0.999 |
| **everything in float32 (v3)** | **1.00000** | 1.00000 | 1.00000 |

The layer norms are not the problem (Core ML evidently keeps their
statistics wide: a float16 variance would overflow at these values and give
cosine 0.6, emulated in PyTorch), nor the attention. The MLP is most of it,
the residual additions the rest, and only the whole graph in float32
reaches the 0.999 the search smoke requires.

What it costs, batch 8, 1600×1200 sources, two rounds each:

| | v2 arithmetic | v3 |
|---|---|---|
| ONNX Runtime CPU, 8 threads, Core Ultra 7 265K | 10.3–10.5 img/s | 11.0–11.2 img/s |
| DirectML, RTX 5060 Ti | 168–174 img/s | 100–101 img/s |
| Core ML on the runner (CPU backend; 4 and 6 runs) | 14–28 img/s | 7.4–11.8 img/s |
| `vision.onnx` | 186.10 MB | 186.15 MB |

The CPU provider loses nothing (it computed in float32 already, minus some
float16 round trips). DirectML loses 40% of its raw speed; the release
build's whole pipeline was 26 pictures a second end to end (300 screenshots
searchable in 11.5 s), so thumbnails, not embeddings, set the pace there.
An integrated GPU, where float16 is twice float32, was not measured.

The owner chose (2026-10-17) to keep DirectML at full speed: the manifest
lists v2's vision file for Windows and Linux and v3's for macOS, one
manifest for every build, narrowed by the library at run time. Windows
measured again with v2's file through the store: DirectML 152–167 img/s at
batch 8 over three rounds, the v2 rate.

## Cutting the download (2026-10-16)

The rule: adopt a variant only if every query keeps its first hit and the
cosine against PyTorch stays at or above 0.995; a vision variant must also
save at least 60 MB.

Cosine against PyTorch float32 is the worst of the ten verification
screenshots and ten sentences. The nine ground-truth queries are checked
two ways:

- **In the app** (`--app-index`). The release app's v1 run stored 336 image
  vectors, made from its own thumbnails through its own preprocessing. A
  text-only change leaves them valid, so v2's text tower is scored against
  them exactly as the app scores. v1 reproduces the coordinator's run to
  three decimals (lakes `ThemeC-img30` 0.149, `ThemeC-img29` 0.110,
  `Spotlight-img50` 0.085, the swatches, `TouchKeyboardThemeLight002` 0.138,
  `ThemeA-img22` 0.132).
- **In Python**, both towers, needed for a vision change: PIL resizes the
  originals, so scores differ from the app's by up to 0.02. On "a glowing
  planet in dark space" this pipeline puts `ThemeA-img23` 0.0019 ahead of
  `ThemeA-img22` for v1 and PyTorch alike, so variants are compared with
  v1 in the same pipeline.

The last column adds 200 screenshots and 16 queries (25 × 236).

| Variant | vision | text | Download | Image cos | Text cos | Nine queries, in the app | Nine queries, Python: same first hit, top-5 overlap | 25 × 236: same first hit, top-5 mean | Verdict |
|---|---|---|---|---|---|---|---|---|---|
| v1, float16 | 186 MB | 565 MB | 755 MB | 0.99999 | 1.00000 | — | — | — | was shipped |
| **int8 token table** | 186 MB | **369 MB** | **559 MB** | 0.99999 | **0.99928** | **9/9 first hits, every group held, top five identical in order; scores within 0.002** | 9/9, 5/5 | 23/25, 4.96 | **shipped as v2** |
| vision MatMuls int8, dynamic | 101 MB | 565 MB | 670 MB | 0.71842 | 1.00000 | | 7/9, min 3/5 | 11/25, 2.16 | rejected |
| the same, per-channel weights | 102 MB | 565 MB | 671 MB | 0.72930 | 1.00000 | | 6/9, min 3/5 | 9/25, 2.4 | rejected |
| vision MatMul weights int8, float16 compute | 97 MB | 565 MB | 666 MB | 0.99928 | 1.00000 | | 9/9, 5/5 | 20/25, 4.96 | passes; not shipped |
| both int8 (table and vision weights) | 97 MB | 369 MB | 470 MB | 0.99928 | 0.99928 | | 9/9, 5/5 | 20/25, 4.96 | not shipped |
| *PyTorch float32, for scale* | | | | 1 | 1 | | 9/9, 5/5 | 24/25, 4.96 | |

The passing variants changed no first hit on the nine queries. In the
25 × 236 check every first hit they changed was a near tie in v1, all on
screenshots: the int8 table changed two (v1 margins 0.0004 and 0.0001;
PyTorch float32 changes one, margin 0.0001), the weight-only vision
variant five (margins at or under 0.0024).

### Why the int8 table works

It is the model's only large weight that is read by lookup rather than
multiplied: a sentence reads 64 of its 256,000 rows, so the int8 error
enters once, at the input, instead of in every layer's arithmetic. Per-row
int8 keeps every row's direction to cosine ≥ 0.99957 (RMS error 0.9% of the
weights), and the graph widens only the rows it reads
(`Gather(int8) → Cast → Mul(Gather(row scale))`), so the 197 MB table is
never expanded in memory. On the CPU the text tower's private memory after
load fell from 1,144 MB to 555 MB, load time from 1.4–1.9 s to 0.9–1.2 s,
and a sentence takes the same 22–26 ms (numbers in `search/README.md`).

### Why dynamic int8 breaks the vision tower

Dynamic quantisation gives each activation tensor one uint8 scale per call.
SigLIP2's MLP down-projections receive a few huge activations among
ordinary ones, measured on the ten verification images:

| Input of | Largest value | 99.9th percentile | Largest / p99.9 |
|---|---|---|---|
| layer 9 `fc2` | 1,729 | 5.3 | 326 |
| layer 5 `fc2` | 223 | 1.4 | 159 |
| layer 10 `fc2` | 324 | 8.0 | 41 |
| layer 11 `fc2` | 213 | 12.8 | 17 |
| head `fc1` (for contrast) | 7.7 | 5.6 | 1.4 |

With a step of 1,729 / 255 ≈ 6.8, nearly every ordinary value in layer 9
rounds to zero. Leaving layers out does not rescue it: all `fc2` in float
gives cosine 0.943, the six worst inputs in float 0.965, the whole MLP in
float 0.978, all below 0.995.

Weight-only int8 avoids the problem because activations are never
quantised: 74 weight MatMuls are stored as int8 with one scale per output
column (column cosine ≥ 0.99742) and rebuilt to float16 in the graph. The
rebuild is float32 (`Cast → Mul → Cast`) because ONNX Runtime folds
constants on the CPU provider, which has no float16 `Mul`; a float16
rebuild does not fold and would run on every call. Folded, the session
holds the same float16 weights as v1 (same private memory, 383 MB), and
loading takes about 0.25 s longer (median 1.40 s against 1.15 s).

### Why the weight-only vision variant is not in v2

It passes the rule (cosine 0.99928, nine of nine first hits, 89 MB saved),
but shipping it needs a new `modelId`, and a new id fails at start-up
today: `SearchOptions.ModelId` in the frozen Abstractions defaults to
`siglip2-base-patch16-256`, the core does not set it, and
`AddTesseraSearch` throws when the embedded manifest names another id. It
also makes every existing install re-embed its library and download all
three files into a new folder. v2 takes the text-only cut, which needs
none of that; the vision cut (559 → 470 MB) waits for that decision.

## Verification of the float16 export (2026-10-16, ten screenshots and ten sentences)

Cosine between the ONNX output and PyTorch float32, worst case over the ten:

| Variant | Image | Text | Vision img/s (CPU, batch 1) | Text ms |
|---|---|---|---|---|
| float32 | 1.00000 | 1.00000 | 7.4 | 46.9 |
| **float16 (v1)** | 0.99999 | 1.00000 | 10.9 | 39.2 |
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
