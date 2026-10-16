using System.Diagnostics;
using System.Globalization;
using System.Numerics.Tensors;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using Tessera.Desktop.Search;

namespace Tessera.Smoke;

/// <summary>
/// One tower in a raw ONNX Runtime session on CoreML, with the batch fixed
/// and the CoreML options given on the command line, compared picture by
/// picture (or sentence by sentence) with a CPU session on the same input.
/// CI runs it over candidate graphs and compute units to choose what the
/// search library ships; the library's own path is <c>tessera-smoke search</c>.
/// </summary>
internal static class CoreMlProbe
{
    private static readonly string[] Sentences = ["a solid red square", "a photo of a dog on a beach", "a city skyline at night"];

    /// <param name="loads">Sessions to open one after the other; with a cache directory the second shows a warm start.</param>
    public static int Run(string onnx, int batch, string units, string format, string? cache, int loads, string? label)
    {
        var ort = OrtEnv.Instance().GetVersionString();
        var name = label ?? Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(onnx)));
        var report = new Report($"CoreML probe: {name}/{Path.GetFileName(onnx)}, {format}, {units}, batch {batch}"
            + $"{(cache is null ? "" : ", cached")} (ONNX Runtime {ort})");

        using var cpuOptions = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        using var cpu = new InferenceSession(onnx, cpuOptions);
        var inName = cpu.InputMetadata.Keys.First();
        var outName = cpu.OutputMetadata.Keys.First();
        var meta = cpu.InputMetadata[inName];
        var text = meta.ElementType == typeof(long);
        var dim = meta.SymbolicDimensions.FirstOrDefault(d => !string.IsNullOrEmpty(d)) ?? "batch";
        if (text)
        {
            batch = 1;
        }

        var (inputs, labels) = text ? TextInputs(onnx) : ImageInputs(batch);
        var reference = inputs.Select(i => RunOnce(cpu, inName, outName, i)).ToArray();
        var cpuMs = Time(cpu, inName, outName, inputs[0]);

        for (var load = 1; load <= loads; load++)
        {
            using var so = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,   // the partition count
            };
            so.AddFreeDimensionOverrideByName(dim, batch);
            var providerOptions = new Dictionary<string, string> { ["ModelFormat"] = format, ["MLComputeUnits"] = units };
            if (cache is not null)
            {
                Directory.CreateDirectory(cache);
                providerOptions["ModelCacheDirectory"] = cache;
            }

            var sw = Stopwatch.StartNew();
            InferenceSession coreml;
            try
            {
                so.AppendExecutionProvider("CoreML", providerOptions);
                coreml = new InferenceSession(onnx, so);
            }
            catch (Exception e) when (e is OnnxRuntimeException or NotSupportedException or EntryPointNotFoundException)
            {
                var message = e.Message.ReplaceLineEndings(" ");
                report.Check(false, $"load {load}: CoreML session", message.Length > 600 ? message[..600] : message);
                return report.Finish();
            }

            using (coreml)
            {
                report.Number($"load {load}: CoreML session", sw.Elapsed.TotalSeconds, "s");
                float[][] got;
                try
                {
                    got = inputs.Select(i => RunOnce(coreml, inName, outName, i)).ToArray();
                }
                catch (OnnxRuntimeException e)
                {
                    report.Check(false, $"load {load}: CoreML run", e.Message.ReplaceLineEndings(" "));
                    return report.Finish();
                }

                var dims = reference[0].Length / batch;
                var cosines = new List<double>();
                for (var r = 0; r < inputs.Length; r++)
                {
                    for (var i = 0; i < batch; i++)
                    {
                        cosines.Add(TensorPrimitives.CosineSimilarity(got[r].AsSpan(i * dims, dims), reference[r].AsSpan(i * dims, dims)));
                    }
                }

                if (load == 1)
                {
                    report.Number("CoreML against CPU, cosine per item",
                        string.Join(", ", cosines.Select((c, i) => $"{labels[i]} {c.ToString("F5", CultureInfo.InvariantCulture)}")));
                    var coremlMs = Time(coreml, inName, outName, inputs[0]);
                    report.Number($"ms per run, CoreML / CPU (batch {batch})", $"{coremlMs:F1} / {cpuMs:F1}");
                    if (!text)
                    {
                        report.Number("images per second, CoreML / CPU", $"{batch * 1000 / coremlMs:F1} / {batch * 1000 / cpuMs:F1}");
                    }
                }

                report.Number($"load {load}: lowest cosine", cosines.Min(), "", 5);
                report.Check(cosines.Min() >= 0.999, $"load {load}: CoreML matches the CPU (cosine >= 0.999)",
                    cosines.Min().ToString("F5", CultureInfo.InvariantCulture));
            }
        }

        if (cache is not null)
        {
            var files = Directory.GetFiles(cache, "*", SearchOption.AllDirectories);
            report.Number("cache directory", $"{files.Length} files, {files.Sum(f => new FileInfo(f).Length) / 1048576.0:F0} MB");
        }

        return report.Finish();
    }

    /// <summary>Red, green and blue swatches and generated pictures, through the library's own preprocessing.</summary>
    private static (OrtValue[] Inputs, string[] Labels) ImageInputs(int batch)
    {
        var manifest = ModelManifest.Embedded();
        var size = manifest.ImageSize;
        var per = ImagePreprocessor.InputLength(size);
        var bitmaps = new List<(string, SKBitmap)>
        {
            ("red", Images.Solid(new SKColor(255, 0, 0), 256, 256)),
            ("green", Images.Solid(new SKColor(0, 255, 0), 256, 256)),
            ("blue", Images.Solid(new SKColor(0, 0, 255), 256, 256)),
        };
        for (var k = 0; bitmaps.Count < 2 * batch; k++)
        {
            bitmaps.Add(($"picture{k}", Images.Picture(200 + k, 640, 480)));
        }

        var runs = new List<OrtValue>();
        for (var start = 0; start < bitmaps.Count; start += batch)
        {
            var pixels = new float[batch * per];
            for (var i = 0; i < batch; i++)
            {
                ImagePreprocessor.Fill(bitmaps[start + i].Item2, size, pixels.AsSpan(i * per, per));
            }

            runs.Add(OrtValue.CreateTensorValueFromMemory(pixels, [batch, 3, size, size]));
        }

        foreach (var (_, b) in bitmaps)
        {
            b.Dispose();
        }

        return ([.. runs], [.. bitmaps.Select(b => b.Item1)]);
    }

    private static (OrtValue[] Inputs, string[] Labels) TextInputs(string onnx)
    {
        var manifest = ModelManifest.Embedded();
        var tokenizer = GemmaTextTokenizer.FromFile(Path.Combine(Path.GetDirectoryName(onnx)!, "tokenizer.model"), manifest.Tokenizer);
        var inputs = Sentences.Select(s => OrtValue.CreateTensorValueFromMemory(tokenizer.Encode(s), [1, tokenizer.MaxLength])).ToArray();
        return (inputs, Sentences.Select(s => $"\"{s}\"").ToArray());
    }

    private static float[] RunOnce(InferenceSession session, string inName, string outName, OrtValue input)
    {
        using var runOptions = new RunOptions();
        using var outputs = session.Run(runOptions, [inName], [input], [outName]);
        var output = outputs[0];
        if (output.GetTensorTypeAndShape().ElementDataType == TensorElementType.Float16)
        {
            var half = output.GetTensorDataAsSpan<Half>();
            var result = new float[half.Length];
            for (var i = 0; i < half.Length; i++)
            {
                result[i] = (float)half[i];
            }

            return result;
        }

        return output.GetTensorDataAsSpan<float>().ToArray();
    }

    /// <summary>Median of five runs after one warm-up, in milliseconds.</summary>
    private static double Time(InferenceSession session, string inName, string outName, OrtValue input)
    {
        RunOnce(session, inName, outName, input);
        var times = new List<double>();
        for (var k = 0; k < 5; k++)
        {
            var sw = Stopwatch.StartNew();
            RunOnce(session, inName, outName, input);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        return times[2];
    }
}
