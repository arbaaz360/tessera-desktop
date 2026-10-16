# search — Tessera.Desktop.Search

SigLIP2 on ONNX Runtime and a brute-force vector index, behind the frozen
interfaces in `Tessera.Desktop.Search.Abstractions` (contract §6). The core
registers it with `services.AddTesseraSearch(new SearchOptions { ModelsDir = … })`
and gets `IImageEmbedder` and `IVectorIndex` as singletons.

| Project | What |
|---|---|
| `src/Tessera.Desktop.Search.Abstractions` | the interfaces; written by the coordinator, frozen |
| `src/Tessera.Desktop.Search` | the implementation |
| `tests/Tessera.Desktop.Search.Tests` | xunit; 44 tests |
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
| `ModelManifest` | the embedded `models/manifest.json`: file names, sizes, sha256, URLs, tokenizer facts, input shapes. From version 4 a file may be listed per platform (`platforms`: Windows and Linux get v2's `vision.onnx`, macOS v3's); `ForPlatform` narrows it and `Embedded()` returns this platform's |
| `ModelStore` | makes `<ModelsDir>/<modelId>/` hold every manifest file for this platform: hashes what is there, downloads what is missing or wrong to a `.part` with Range resume and progress, verifies, renames into place, writes `manifest.json` beside them. Three attempts; a hash mismatch deletes the part. One download at a time; concurrent callers wait on it |
| `SiglipEmbedder` | `IImageEmbedder`. Loads both towers once; DirectML on Windows when `PreferGpu` and a hardware adapter exists (the one with the most video memory), on macOS with `PreferGpu` the CPU at once and CoreML swapped in when it has loaded (below), CPU otherwise and whenever a GPU provider fails to initialise. `Device` says which, now. `DeviceUpgrade` completes when the macOS move is over. `EmbedImages` and `EmbedTexts` take a batch; on CoreML the sessions take exactly 8 images and 1 sentence, so short batches are padded and long ones chunked (`FixedImageBatch` says so) |
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

v3's vision tower computes in float32 so that Core ML matches the CPU
(`models/README.md`); on DirectML that cost 40%, so Windows keeps v2's
file (manifest version 4). Measured with the GPU otherwise idle, 8 CPU
threads:

| Vision, batch 8 | v2's file (Windows from manifest v4) | v3's file (macOS) |
|---|---|---|
| CPU | 10.3 – 10.5 img/s (8.7 – 9.4 in the full test run) | 11.0 – 11.2 img/s |
| DirectML, RTX 5060 Ti | 168 – 174 img/s; 152 – 167 again through the store from manifest v4 (three rounds) | 100 – 101 img/s |
| Text, CPU / DirectML (the same tower) | 25 – 29 / 6.5 – 8.0 ms | the same |

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

#### CPU first, CoreML when it has loaded

A CoreML load takes minutes on the runner, so on macOS `EnsureReadyAsync`
opens the CPU sessions and returns ready, and CoreML compiles and loads on
a background task. When it is ready the embedder swaps one counted session
set for the other: a call takes a reference to the set that is active when
it starts and keeps it to the end, so calls already running finish on the
CPU and later calls use CoreML; the CPU set is disposed when the last call
using it returns. `Device` turns from `Cpu` to `CoreML`. If CoreML fails
the embedder stays on the CPU and logs why. The two sides' vectors agree
(below), so an index built partly on each is one index. Windows (DirectML
or CPU) loads as before, in one step.

On the `macos-latest` runner (macOS 26, Apple silicon, 3 cores, ONNX Runtime
1.24.4), runs
[38968062311](https://github.com/arbaaz360/tessera-desktop/actions/runs/38968062311)
and [38971746054](https://github.com/arbaaz360/tessera-desktop/actions/runs/38971746054):

| A Mac start, `PreferGpu` true | First launch | Second launch |
|---|---|---|
| Ready, on the CPU (sha256 of the model files, both CPU sessions) | 3.4 – 6.3 s | 2.2 – 2.3 s |
| First search answered, from the start of the load (red first) | 8.1 – 14.1 s | |
| Moved to CoreML | 261 – 330 s after ready (vision compiled in 153 s, text in 108 s) | 125 – 145 s after ready, from the cache (vision 62 s, text 60 s) |
| Inside the published core: `/api/search` answers, from process start | 8.1 – 9.1 s, device `cpu` | |
| Inside the published core: `/api/search/status` says `coreml` | 209 – 274 s after start | |
| The swap with calls running (`SwapsSessionsWhileEmbedsRun`, second CPU set) | 18 calls across it, cosine 1.000000; Windows 37 – 39 | |

| Once loaded | CoreML | CPU |
|---|---|---|
| "a solid red square" over red, green, blue swatches | red 0.1609, blue 0.1174, green 0.1026 | red 0.1612, blue 0.1176, green 0.1027 |
| Vectors against each other (red, green, blue, sentence) | 1.00000, 1.00000, 1.00000, 0.99996 | |
| Images/s, batch 8, 640×480 sources | 8.5 – 12.9 (the test: 9.8 from 1600×1200) | 1.2 – 1.7 |
| One image alone (padded to 8) | 1.33 img/s | 1.62 img/s |
| Text, median of 10 | 9.5 – 15.2 ms | 125 – 147 ms |
| Cache on disk | 2,590 MB (79 files) | |

Ranges in the second table are over runs 38953158750, 38956260548,
38968062311 and 38971746054. The CoreML load itself is Core ML's: loading
the cached compiled vision model with Core ML alone
(`tools/smoke/coreml_load.swift`, no ONNX Runtime) took 99 – 144 s, 48 –
56 s again, 79 – 97 s CPU-only. Core ML on the runner lists a
GPU ("Apple Paravirtual device") but its compute plan put all 536 ops on
the CPU. A real Mac is likely faster, and unmeasured.

#### What the cache holds

ONNX Runtime writes, per partition, the ML package it generated
(`model/`, with `Data/com.microsoft.OnnxRuntime/model.mlmodel`) and Core
ML's compiled model inside it (`model/compiled_model.mlmodelc`). Run
38968062311:

| Tower | On disk | Compiled `model.mil` | Package `model.mlmodel` |
|---|---|---|---|
| vision (partition 0 of 4) | 1.4 GB | 1,012 MB | 329 MB |
| text (partition 1 of 5) | 1.1 GB | 998 MB | 162 MB |

The weights are inside `model.mil`, a text format: of the vision
program's 519 constants, 443 are written inline as decimal numbers and 76
point at `weights/weight.bin`, which is 3.5 MB (vision) and 1.2 MB at most
(text). About 1 GB of text per tower (93 M float32 vision weights, 86 M
float16 text weights) is both the size and the likely reason a load from
the cache still takes a minute per tower. Why ONNX Runtime's converter
inlined these initializers, when its own rule sends tensors of ten or more
elements to the weight file, was not found; binary weights would be about
a third of the size.

Options, measured in run 38971746054:

| | Disk | Effect |
|---|---|---|
| Keep the cache as it is (shipped) | 2.6 GB | CoreML 125 – 145 s after a launch instead of 261 – 330 s |
| Delete the uncompiled packages (`model/Data`) after a load | about 2.0 GB (vision 1.4 → 1.0 GB, text − 0.16 GB) | loads the same: vision from the pruned copy in 90.5 s, vectors 1.00000 |
| No cache | 0 | CoreML 261 – 330 s after every launch; search is on the CPU meanwhile, never blocked |

Not changed. Pruning works with ONNX Runtime 1.24.4, but only because
it checks that `model/` exists and then reads `compiled_model.mlmodelc`
inside it: an undocumented layout (the folder name carries the runtime
version, and a cache that fails to load is compiled again, so a mistake
would cost one compile, not a broken search). Dropping the cache trades
2.6 GB on every Mac for a later CoreML at every launch; a real Mac's
numbers should decide it.

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
| Across the CPU-to-CoreML swap (first search on the CPU vs after the move, runner) | 1.00000, 1.00000, 1.00000, 0.99996 |
| Windows' v2 vision vs macOS' v3 vision, the five fixtures (export) | ≥ 0.9999946 |
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
| Size, manifest v4 | 559 MB on each platform (vision 186, text 369, tokenizer 4): Windows gets v2's `vision.onnx`, macOS v3's. Windows upgrading from v2 downloads nothing; a Windows folder holding v3's vision gets v2's back (186 MB, 3.3 s here) |
| Download on the macOS runner, no token, verified | 559 MB in 6 to 10 s |
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
- **CoreML arrives late on the runner and its cache is large**: search
  answers on the CPU within seconds, but CoreML takes over 261 s after the
  first launch and 125 s after later ones, from a 2.6 GB cache whose
  compiled models hold their weights as text (above). Whether to keep the
  cache, and whether a real Mac loads fast enough to drop it, needs a real
  Mac.
- **`IImageEmbedder.Device` now changes once on macOS** (`Cpu`, then
  `CoreML`). The frozen interface says "the device chosen at load time";
  the core reads it per request, so `/api/search/status` follows, but the
  contract's wording is the coordinator's to update.
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
