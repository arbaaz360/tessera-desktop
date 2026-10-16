# search — Tessera.Desktop.Search

SigLIP2 on ONNX Runtime and a brute-force vector index, behind the frozen
interfaces in `Tessera.Desktop.Search.Abstractions` (contract §6). The core
registers it with `services.AddTesseraSearch(new SearchOptions { ModelsDir = … })`
and gets `IImageEmbedder` and `IVectorIndex` as singletons.

| Project | What |
|---|---|
| `src/Tessera.Desktop.Search.Abstractions` | the interfaces; written by the coordinator, frozen |
| `src/Tessera.Desktop.Search` | the implementation |
| `tests/Tessera.Desktop.Search.Tests` | xunit; 41 tests |
| `../models` | the export script, the manifest, the published model files |

## Build and test

```bash
dotnet build search
dotnet test search
```

Tests that need the model load it from `models/out` (the export output) or
`TESSERA_MODELS_DIR`; without either they print "skipped" and pass. The
qualitative check reads `TESSERA_QUALITATIVE_DIR`, a folder of non-personal
images. The timing tests write their numbers to
`search/tests/out/*.json` (gitignored).

Environment variables, all optional, none used by the product:

| Variable | Meaning |
|---|---|
| `TESSERA_MODELS_DIR` | a folder with `vision.onnx`, `text.onnx`, `tokenizer.model` for the tests |
| `TESSERA_QUALITATIVE_DIR` | a folder of non-personal images for the qualitative test |
| `TESSERA_MODELS_TOKEN` | a GitHub token; lets `ModelStore` fetch release assets through the API from a private repository. Not needed for the shipped manifest, whose files are in the public `arbaaz360/siglip2-base-onnx`. Sent to github.com hosts only |
| `TESSERA_REAL_DOWNLOAD=1` | one test downloads the real 559 MB release into `%TEMP%` with no token, as a buyer's first run does, and verifies it |
| `TESSERA_CPU_THREADS` | thread count for the CPU throughput test |

## What it does

| Class | Does |
|---|---|
| `ModelManifest` | the embedded `models/manifest.json`: file names, sizes, sha256, URLs, tokenizer facts |
| `ModelStore` | makes `<ModelsDir>/<modelId>/` hold every manifest file: hashes what is there, downloads what is missing or wrong to a `.part` with Range resume and progress, verifies, renames into place, writes `manifest.json` beside them. Three attempts; a hash mismatch deletes the part. One download at a time; concurrent callers wait on it |
| `SiglipEmbedder` | `IImageEmbedder`. Loads both towers once; DirectML on Windows when `PreferGpu` and a hardware adapter exists (the one with the most video memory), CoreML on macOS when `PreferGpu` (below), CPU otherwise and whenever a GPU provider fails to initialise. `Device` says which. `EmbedImages` and `EmbedTexts` take a batch; on CoreML the sessions take exactly 8 images and 1 sentence, so short batches are padded and long ones chunked (`FixedImageBatch` says so) |
| `ImagePreprocessor` | any `SKBitmap` → 256×256 squash-resize (no crop), `(x/255 − 0.5)/0.5`, NCHW float32 |
| `GemmaTextTokenizer` | the Gemma SentencePiece model through `Microsoft.ML.Tokenizers`; no BOS, EOS appended, right-padded with 0 to 64, content truncated so the EOS fits |
| `BruteForceVectorIndex` | `IVectorIndex`. int8 rows with one scale each, float accumulation (AVX2+FMA, else `Vector128`, else scalar), filter before top-k, parallel over 4096-row blocks, reader-writer lock, one-file save/load (`TSVX`, version 1) |
| `DirectMLDevices` | DXGI enumeration so DirectML is never chosen for WARP, the software adapter |

The tokenizer needed no fallback: `Microsoft.ML.Tokenizers` loads the Gemma
BPE model and reproduces the reference input ids exactly on all 14 fixture
sentences, including a 200-token truncation and non-Latin text.

The int8 scale is `1/|q|` where `q` is the int8 row, so `dot(query, q) ×
scale` is exactly the cosine between the query and the vector the row
reconstructs. Measured error against float cosine: at most 0.001 over
10,000 pairs.

## Numbers

Machine: Intel Core Ultra 7 265K (8 P-cores + 12 E-cores, 20 threads),
64 GB, NVIDIA GeForce RTX 5060 Ti 16 GB, Windows 11. ONNX Runtime from the
DirectML package. The GPU was running another project's job at 100% during
every measurement, so the DirectML figures are a floor.

### Embedding

Sources are 1600×1200 bitmaps, so each figure includes decode-free
preprocessing (about 3 ms an image).

| | Images/s, batch 1 | Images/s, batch 8 | Text embed, ms |
|---|---|---|---|
| CPU, ONNX Runtime default threads (20) | 6.0 – 7.2 | 6.1 – 10.5 | 39 – 47 |
| CPU, 16 threads | 8.1 – 17.2 | 12.9 – 16.6 | 27 – 36 |
| CPU, 12 threads | 15.8 | 14.7 | 23 |
| CPU, 8 threads | 11.2 | 10.3 | 27 |
| DirectML, RTX 5060 Ti | 16.6 | 116 – 139 | 6.9 |
| DirectML, Intel integrated graphics | 1.7 | 19 | 27 |

Ranges are the spread over three to five runs of a six-second loop each;
another project's training job shared the machine throughout, which is
most of the spread.

On this hybrid CPU the runtime's default (one thread per core, E-cores
included) is the slowest setting; `SearchOptions.CpuThreads = 16` is
faster. The library leaves the default alone because the right number
depends on the CPU; the host may set it. The core's embedding worker should
batch (eight is enough): it is the difference between 17 and 116 images a
second on the GPU.

A 50,000-image library at CPU batch-8 speed embeds in about 80 minutes; on
the RTX 5060 Ti in about 7.

Model v3 computes the vision tower in float32 so that Core ML matches the
CPU (`models/README.md`). Measured the same day with the GPU otherwise
idle, two rounds each, 8 CPU threads:

| Vision, batch 8 | v2 arithmetic | v3 |
|---|---|---|
| CPU | 10.3 – 10.5 img/s | 11.0 – 11.2 img/s |
| DirectML, RTX 5060 Ti | 168 – 174 img/s | 100 – 101 img/s |
| Text, CPU / DirectML (unchanged tower) | 25 / 6.5 – 6.7 ms | the same |

Model v2 changed only the text tower (its token table is int8; see
`models/README.md`). Measured in a fresh process per file, five interleaved
rounds, 8 threads, Python ONNX Runtime on the CPU:

| Text tower | v1 | v2 |
|---|---|---|
| File | 565 MB | 369 MB |
| Private memory after load | 1,144 MB | 555 MB |
| Load | 1.4 – 1.9 s | 0.9 – 1.2 s |
| One sentence, median | 23 – 24.5 ms | 22 – 26 ms |
| One sentence on DirectML (.NET test) | 6.9 ms | 5.4 ms |

The CPU provider has no float16 kernels for most operators and widens
float16 weights to float32 when it loads them: v1's 1,144 MB is the float32
size of the whole tower (the float32 export is 1,129 MB). v2's table stays
int8 (197 MB) and only the 64 rows a sentence uses are widened; the rest of
the tower is the other 350 MB.

### CoreML (macOS)

`SiglipEmbedder` opens each tower on CoreML with these provider options
(`CoreMlFormat`, `CoreMlComputeUnits`, `CoreMlSession`):

| Option | Value | Why |
|---|---|---|
| `ModelFormat` | `MLProgram` | the graphs store float16 weights and use `LayerNormalization`; ONNX Runtime's `NeuralNetwork` builder takes float32 only and has no layer norm, and Apple deprecated the format |
| `MLComputeUnits` | `CPUAndGPU` | the vision tower computes in float32, which the Neural Engine does not run, so `ALL` would only add a Neural Engine compile at load. On the runner `ALL`, `CPUAndGPU` and `CPUOnly` gave the same vectors to five decimals: that virtual machine has no GPU or Neural Engine for Core ML |
| batch dimension | fixed by name: vision 8, text 1 (`AddFreeDimensionOverrideByName`) | Core ML refuses an unbounded dimension. The name comes from the manifest (`inputs.*.shape`: `batch`), which the export reads from the graph; eight is what the core's embedding worker hands over. Short batches are padded with zeros and their outputs dropped; a single image goes through the same path, so it costs a batch of eight |
| `ModelCacheDirectory` | `<model dir>/coreml/<tower>-<sha256[..16]>-b<batch>-ort<version>-MLProgram-CPUAndGPU/` | without it ONNX Runtime converts and compiles both towers at every launch. ONNX Runtime keys its cache by a hash of the model's path and reuses whatever is there, so the folder name carries everything else that shapes the compiled model; other folders for the same tower are deleted when a new one is made, and a cache that fails to load is deleted and compiled again once |
| `RequireStaticInputShapes`, `SpecializationStrategy`, `AllowLowPrecisionAccumulationOnGPU` | defaults | shapes are static once fixed; a fast-prediction specialisation cannot be judged without a GPU to run on; low-precision accumulation is what float32 arithmetic is there to avoid |

On the `macos-latest` runner (macOS 26, Apple silicon, 3 cores, ONNX Runtime
1.24.4), model v3, run
[38953158750](https://github.com/arbaaz360/tessera-desktop/actions/runs/38953158750):

| | CoreML | CPU |
|---|---|---|
| Device with `PreferGpu` true / false | `CoreML`, batch fixed at 8 | `Cpu` |
| "a solid red square" over red, green, blue swatches | red 0.1609, blue 0.1174, green 0.1026 | red 0.1612, blue 0.1176, green 0.1027 |
| Vectors against each other (red, green, blue, sentence) | 1.00000, 1.00000, 1.00000, 0.99996 | |
| Images/s, batch 8, 640×480 sources | 10.1 (the test: 9.8 from 1600×1200) | 1.3 |
| One image alone (padded to 8) | 1.33 img/s | 1.62 img/s |
| Text, median of 10 | 12.1 ms | 139.6 ms |
| Load, first time (download verified, both towers compiled) | 221 s: vision 118 s, text 102 s | 3.0 s |
| Load, from the cache | 108 s: vision 57 s, text 51 s | |
| Cache on disk | 2,590 MB (79 files) | |

The CoreML load is the cost to watch. In the probe a vision tower took
173 s cold and still 98 s from the cache in a new process: the cache saves
ONNX Runtime's conversion and Core ML's compile, and the rest is Core ML
preparing the compiled model. On the runner that is CPU-only Core ML in a
virtual machine; a real Mac is likely faster, and unmeasured.

`src-tauri/tools/smoke` holds the tools that chose these:
`tessera-smoke coreml <tower.onnx> --units … --format … --cache …
--option key=value --verbose` runs one tower on CoreML against the CPU,
picture by picture, and `models/coreml_compat.py variants` writes the
candidate graphs.

### Fidelity

| Check | Result |
|---|---|
| ONNX vs PyTorch, ten screenshots (export) | image cosine ≥ 0.99999 |
| ONNX vs PyTorch, ten sentences (export) | text cosine ≥ 0.99928 from model v2 (int8 token table; v1 ≥ 0.99999) |
| .NET vs reference embeddings, 256×256 fixtures | cosine ≥ 0.99999 |
| .NET vs reference embeddings, fixtures that need resizing | cosine ≥ 0.9968 (SkiaSharp trilinear vs PIL bilinear) |
| .NET text vs reference, 14 sentences | cosine ≥ 0.999 (test floor) |
| DirectML vs CPU, same files (`GpuMatchesCpu`) | text ≥ 0.999996, image ≥ 0.9999999 from model v3 (v2: 0.99998) |
| CoreML vs CPU, macOS runner (`GpuMatchesCpu`) | text ≥ 0.99986, image ≥ 0.99999 (model v3; v2 refused to load, and in float16 arithmetic reached 0.983) |
| Fixed batches (CoreML's padding path, run on the CPU) vs dynamic | 1.000000 for 1, 3, 8 and 11 images and for sentences one by one |
| Same image twice | bit-identical vectors |
| "a solid red image" against red, blue, green swatches | red first; blue and green likewise |

### Index, 200,000 random unit vectors of 768 dimensions

| | |
|---|---|
| Upsert 200,000 | 1.5 s |
| Search top-100, median | 12 ms (max 45 ms) |
| Search top-100 with a filter rejecting half, median | 4 ms |
| Search top-10, median | 6 ms |
| RAM | 157 MB (managed heap grew 166 MB; working set 163 MB) |
| File | 156 MB; save 44 ms, load 51 ms |

### Model download

| | |
|---|---|
| Size, model v3 | 559 MB (vision 186, text 369, tokenizer 4); from v2 an install downloads only `vision.onnx` (186 MB): `text.onnx` and `tokenizer.model` are the same bytes |
| Download of v3 on the macOS runner, no token, verified | 559 MB in 9 to 10 s |
| Size, model v2 | 559 MB (vision 186, text 369, tokenizer 4) |
| Size, model v1 | 755 MB (vision 186, text 565, tokenizer 4) |
| Real download of v2 from the public release, no token, verified (2026-10-16) | 73.7 s (61 Mbit/s), all three hashes matched |
| Real download of v1 through the API, verified (2026-10-16) | 177 s (34 Mbit/s), all three hashes matched |

The text tower was three times the vision tower because the Gemma
vocabulary is 256,000 rows; its embedding table alone is 393 MB in float16.
v2 stores that table as int8 with a scale per row (197 MB), which keeps
every first hit of the nine ground-truth queries and halves the text
tower's memory on the CPU. The vision tower can lose another 89 MB with
int8 weights, at the price of a new `modelId`; `models/README.md` has the
numbers and why that is not in v2.

### Qualitative: 200 screenshots, ten queries

Screenshots of application windows are a hard domain for a model trained on
web photos and captions, and the scores are low throughout (SigLIP's
sigmoid training makes cosines small). Rankings where the library had a
matching picture are sensible; where it had none ("a mountain lake") the
best hit is the one Lightroom window that shows a photo, at 0.09.

Images: 200 screenshots of application windows (decode + embed on DirectML: 22.1 s, 9.0 img/s including decode).

| Query | 1st | 2nd | 3rd |
|---|---|---|---|
| a terminal window with code | an untitled window (0.16) | File Explorer (0.15) | a browser dev tool (0.15) |
| a file explorer window listing folders | qBittorrent (0.15) | TreeSize (0.15) | Studio 3T (0.14) |
| a web browser showing a video | qBittorrent (0.15) | Notepad (0.15) | Greenshot (0.14) |
| a spreadsheet with numbers | Notepad++ (0.13) | Notepad++ (0.13) | a LUT picker (0.12) |
| a chat conversation | File Explorer (0.11) | qBittorrent (0.11) | Windows Settings (0.11) |
| an error dialog box | Docker Desktop (0.15) | Notepad++ (0.14) | File Explorer (0.14) |
| a code editor with a dark theme | Obsidian (0.18) | a browser dev tool (0.17) | Studio 3T (0.17) |
| a settings or preferences dialog | File Explorer (0.14) | File Explorer (0.14) | File Explorer (0.14) |
| a download or progress bar | Greenshot (0.16) | Obsidian (0.15) | Greenshot (0.15) |
| a photo of a mountain lake at sunrise | Lightroom Classic, showing a photo (0.09) | File Explorer (0.03) | Obsidian (0.03) |

## Packages and licences

| Package | Licence |
|---|---|
| `Microsoft.ML.OnnxRuntime.DirectML` (win-x64, win-arm64) and `Microsoft.ML.OnnxRuntime` (everything else) | MIT |
| `Microsoft.AI.DirectML` (pulled in by the DirectML package; `DirectML.dll`) | Microsoft Software License Terms; redistribution inside Windows applications permitted (section 1a) |
| `Microsoft.ML.Tokenizers` | MIT |
| `SkiaSharp` | MIT |
| `System.Numerics.Tensors`, `Microsoft.Extensions.*` | MIT |
| The model, `google/siglip2-base-patch16-256` | Apache-2.0 |

The DirectML package replaces the base package on `win-x64` and `win-arm64`
(the csproj condition keys on `RuntimeIdentifier`, falling back to the build
OS when none is set). Both ship the same managed assembly; the DirectML one
adds the provider to the native library. They cannot coexist in one output.
Both pinned to the same version, the newest the DirectML package reaches.

## Not done

- **CoreML has run only on GitHub's macOS runner**, a virtual machine in
  which Core ML sees no GPU and no Neural Engine: every op ran on its CPU
  backend (compute plan: 536 of 536). A real Mac will put work on the GPU;
  its speed, its load time and its vectors against the CPU are unmeasured.
- **A CoreML start is slow on the runner and its cache is large**: 221 s
  for both towers the first time, 108 s from the cache, which takes 2.6 GB
  next to a 559 MB model (above). Search waits for it at every launch. The
  core could answer on the CPU (3 s to load) and swap CoreML in when it is
  ready; vectors from the two agree to 0.99996, so the index does not care.
  Measure on a real Mac first.
- ~~The download URLs answered 404 while the repository was private.~~
  Resolved 2026-10-16: the files moved to the public model-only repository
  `arbaaz360/siglip2-base-onnx` and the manifest points there.
- **A model with a new `modelId` cannot ship yet.** `SearchOptions.ModelId`
  in the frozen Abstractions defaults to `siglip2-base-patch16-256`, the
  core does not set it, and `AddTesseraSearch` throws when the embedded
  manifest names another id. Any vision-tower change (the int8-weight
  vision variant saves 89 MB and passes the quality rule) needs that
  default to follow the manifest, or the core to pass the manifest's id.
- The qualitative set is screenshots because that is what this machine
  holds that is not a person's photos. A reference-library sample from a
  real artist would say more about R-01.
