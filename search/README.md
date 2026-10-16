# search — Tessera.Desktop.Search

SigLIP2 on ONNX Runtime and a brute-force vector index, behind the frozen
interfaces in `Tessera.Desktop.Search.Abstractions` (contract §6). The core
registers it with `services.AddTesseraSearch(new SearchOptions { ModelsDir = … })`
and gets `IImageEmbedder` and `IVectorIndex` as singletons.

| Project | What |
|---|---|
| `src/Tessera.Desktop.Search.Abstractions` | the interfaces; written by the coordinator, frozen |
| `src/Tessera.Desktop.Search` | the implementation |
| `tests/Tessera.Desktop.Search.Tests` | xunit; 37 tests |
| `../models` | the export script, the manifest, the published model files |

## Build and test

```bash
dotnet build search
dotnet test search
```

Tests that need the model load it from `models/out` (the export output) or
`TESSERA_MODELS_DIR`; without either they print "skipped" and pass. The
qualitative check reads `TESSERA_QUALITATIVE_DIR` or `Pictures\Greenshot`
(screenshots on this machine). The timing tests write their numbers to
`search/tests/out/*.json` (gitignored).

Environment variables, all optional, none used by the product:

| Variable | Meaning |
|---|---|
| `TESSERA_MODELS_DIR` | a folder with `vision.onnx`, `text.onnx`, `tokenizer.model` for the tests |
| `TESSERA_QUALITATIVE_DIR` | a folder of non-personal images for the qualitative test |
| `TESSERA_MODELS_TOKEN` | a GitHub token; lets `ModelStore` fetch release assets through the API from a private repository. Not needed for the shipped manifest, whose files are in the public `arbaaz360/siglip2-base-onnx`. Sent to github.com hosts only |
| `TESSERA_REAL_DOWNLOAD=1` | with the token: one test downloads the real 755 MB release into `%TEMP%` and verifies it |
| `TESSERA_CPU_THREADS` | thread count for the CPU throughput test |

## What it does

| Class | Does |
|---|---|
| `ModelManifest` | the embedded `models/manifest.json`: file names, sizes, sha256, URLs, tokenizer facts |
| `ModelStore` | makes `<ModelsDir>/<modelId>/` hold every manifest file: hashes what is there, downloads what is missing or wrong to a `.part` with Range resume and progress, verifies, renames into place, writes `manifest.json` beside them. Three attempts; a hash mismatch deletes the part. One download at a time; concurrent callers wait on it |
| `SiglipEmbedder` | `IImageEmbedder`. Loads both towers once; DirectML on Windows when `PreferGpu` and a hardware adapter exists (the one with the most video memory), CoreML on macOS when `PreferGpu`, CPU otherwise and whenever a GPU provider fails to initialise. `Device` says which. `EmbedImages` and `EmbedTexts` take a batch |
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

### Fidelity

| Check | Result |
|---|---|
| ONNX vs PyTorch, ten screenshots, ten sentences (export) | cosine ≥ 0.99999 |
| .NET vs reference embeddings, 256×256 fixtures | cosine ≥ 0.99999 |
| .NET vs reference embeddings, fixtures that need resizing | cosine ≥ 0.9968 (SkiaSharp trilinear vs PIL bilinear) |
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
| Size | 755 MB (vision 186, text 565, tokenizer 4) |
| Real download of the release through the API, verified | 177 s on this connection (34 Mbit/s), all three hashes matched |

The text tower is three times the vision tower because the Gemma vocabulary
is 256,000 rows; its embedding table alone is 393 MB in float16. Quantising
that one table to int8 would take about 200 MB off the download and is the
obvious next cut if size matters.

### Qualitative: 200 screenshots, ten queries

Screenshots of application windows are a hard domain for a model trained on
web photos and captions, and the scores are low throughout (SigLIP's
sigmoid training makes cosines small). Rankings where the library had a
matching picture are sensible; where it had none ("a mountain lake") the
best hit is the one Lightroom window that shows a photo, at 0.09.

Images: 200 from `Greenshot` (decode + embed on DirectML: 22.1 s, 9.0 img/s including decode).

| Query | 1st | 2nd | 3rd |
|---|---|---|---|
| a terminal window with code | 2026-09-14 15\_40\_06-.png (0.16) | 2026-03-01 20\_13\_29-The\_AI\_Low\_Code - File Explorer.png (0.15) | 2026-09-15 22\_13\_38-The DevTools Pro · Native preview.png (0.15) |
| a file explorer window listing folders | 2026-06-21 21\_45\_06-qBittorrent v5.2.1.png (0.15) | 2026-05-25 11\_53\_58-TreeSize Free.png (0.15) | 2026-08-27 21\_52\_50-Studio 3T Community Edition.png (0.14) |
| a web browser showing a video | 2026-01-11 12\_44\_39-qBittorrent v5.1.4.png (0.15) | 2026-09-24 17\_58\_34-reply-alfonso-4.txt - Notepad.png (0.15) | 2026-09-20 13\_31\_46-Greenshot.png (0.14) |
| a spreadsheet with numbers | 2026-09-18 01\_57\_37-\_new 135 - Notepad++.png (0.13) | 2026-09-17 00\_26\_13-\_new 134 - Notepad++.png (0.13) | 2026-04-27 00\_44\_20-Select a LUT.png (0.12) |
| a chat conversation | 2026-06-08 20\_57\_52-Downloads - File Explorer.png (0.11) | 2026-06-30 23\_05\_26-qBittorrent v5.2.2.png (0.11) | 2026-09-10 22\_31\_17-Settings.png (0.11) |
| an error dialog box | 2026-05-19 01\_19\_55-Docker Desktop.png (0.15) | 2026-09-15 04\_26\_43-\_new 133 - Notepad++.png (0.14) | 2026-07-20 04\_31\_48-Music - File Explorer.png (0.14) |
| a code editor with a dark theme | 2026-09-11 16\_30\_36-Secrets - MyGeneralVault - Obsidian 1.13.7.png (0.18) | 2026-09-15 22\_14\_13-The DevTools Pro · Native preview.png (0.17) | 2026-09-06 15\_04\_05-Studio 3T Community Edition.png (0.17) |
| a settings or preferences dialog | 2026-07-23 00\_34\_01-shared\_data - File Explorer.png (0.14) | 2026-06-22 01\_49\_52-GenericDetector - File Explorer.png (0.14) | 2026-09-25 17\_26\_21-48 Laws - 2026-09-25 - File Explorer.png (0.14) |
| a download or progress bar | 2026-09-20 13\_31\_46-Greenshot.png (0.16) | 2026-09-27 12\_10\_06-Secrets - MyGeneralVault - Obsidian 1.13.7.png (0.15) | 2026-09-28 15\_49\_21-Greenshot.png (0.15) |
| a photo of a mountain lake at sunrise | 2026-05-30 20\_12\_09-Lightroom Catalog - Adobe Photoshop Lightroom Classic - Develop.png (0.09) | 2026-09-20 02\_39\_11-urlbird - File Explorer.png (0.03) | 2026-09-27 13\_12\_55-Secrets - MyGeneralVault - Obsidian 1.13.7.png (0.03) |

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

- **macOS is untested.** The CoreML branch is written against the ORT
  string-keyed provider API (`AppendExecutionProvider("CoreML", …)`) and
  compiles; nobody has run it. Expect to touch `MLComputeUnits` and the
  model format once a Mac is available.
- ~~The download URLs answered 404 while the repository was private.~~
  Resolved 2026-10-16: the files moved to the public model-only repository
  `arbaaz360/siglip2-base-onnx` and the manifest points there.
- **`IImageEmbedder` has no batch method.** `SiglipEmbedder.EmbedImages`
  exists on the class; the core only sees the interface. A batch method on
  the interface would let the worker get the 7× DirectML gain without
  casting to the concrete type.
- The qualitative set is screenshots because that is what this machine
  holds that is not a person's photos. A reference-library sample from a
  real artist would say more about R-01.
